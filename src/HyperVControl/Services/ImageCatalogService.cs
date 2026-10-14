using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace HyperVControl.Services;

/// <summary>Arguments of <see cref="ImageCatalogService.DeployAsync"/>: a VM created from a library entry with the lab defaults.</summary>
internal sealed record ImageDeployRequest(string ImageName, string VmName, bool Linked = true, int MemoryMb = 4096, int Processors = 2,
    string? SwitchName = null, string? Notes = null, bool Start = false, string? VmPath = null);

/// <summary>
/// Image catalogs (Microsoft's Quick Create gallery feed plus custom catalogs), the local image library, fetch
/// jobs and deployment. Downloads, hashing, extraction and disk-space checks are C#; host work (default paths,
/// the in-use guard, the per-VM disk, lab defaults) runs as one <see cref="HyperVScript"/> per step.
/// </summary>
internal sealed class ImageCatalogService(PowerShellRunner runner, JobRunner jobs)
{
    /// <summary>Overrides the library root (default <c>&lt;Get-VMHost VirtualHardDiskPath&gt;\HyperVControl\Images</c>).</summary>
    public const string RootVariable = "HYPERV_CONTROL_IMAGE_ROOT";
    /// <summary>Microsoft's fixed ID of the Default Switch; its name is localized.</summary>
    internal const string DefaultSwitchId = "c08cb7b8-9b3c-408e-8e30-5e16a3aeb444";
    private static readonly string[] OsFamilies = ["windows", "linux", "other"];
    private static readonly string[] Kinds = ["image", "template"];
    private static readonly TimeSpan FeedLifetime = TimeSpan.FromMinutes(5);
    private const int MaxFeedBytes = 16 << 20;
    private static readonly HttpClient Http = CreateHttpClient();

    internal PowerShellRunner Runner { get; } = runner;
    internal JobRunner Jobs { get; } = jobs;
    /// <summary>How long a download may receive nothing before it counts as interrupted (tests shorten it).</summary>
    internal TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    internal Version HostVersion { get; set; } = Environment.OSVersion.Version;

    private readonly SemaphoreSlim hostGate = new(1, 1);
    private readonly SemaphoreSlim fetchGate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> activeFetches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, CatalogFeed Feed)> feeds = new(StringComparer.OrdinalIgnoreCase);
    private HostPaths? hostPaths;

    private sealed record HostPaths(string VirtualHardDiskPath, string VirtualMachinePath);
    private sealed record Recommendation(CatalogImage? Linux, CatalogImage? Windows, ImageLibraryEntry? Deploy, object Summary);

    private const string VmProjection = "($vm | Select-Object Id,Name,@{Name='State';Expression={[string]$_.State}},Generation,ProcessorCount,MemoryStartup,DynamicMemoryEnabled,"
        + "@{Name='CheckpointType';Expression={[string]$_.CheckpointType}},AutomaticCheckpointsEnabled,"
        + "@{Name='EnhancedSessionTransportType';Expression={[string]$_.EnhancedSessionTransportType}},Path,Notes)";

    // Walks each disk's parent chain (current disks, checkpoint disks, recorded children) looking for $target.
    private const string InUseGuard = """
        function Test-ControlChain([string]$Start) {
            $current = $Start
            for ($depth = 0; $current -and $depth -lt 64; $depth++) {
                try {
                    if ([string]::Equals([System.IO.Path]::GetFullPath($current), $target, [System.StringComparison]::OrdinalIgnoreCase)) { return $depth }
                    $current = [string](Get-VHD -Path $current -ErrorAction Stop).ParentPath
                } catch { return -1 }
            }
            return -1
        }
        $references = New-Object System.Collections.ArrayList
        foreach ($machine in @(Get-VM)) {
            $paths = @(Get-VMHardDiskDrive -VM $machine | ForEach-Object { [string]$_.Path })
            foreach ($snapshot in @(Get-VMSnapshot -VM $machine)) { $paths += @(Get-VMHardDiskDrive -VMSnapshot $snapshot | ForEach-Object { [string]$_.Path }) }
            foreach ($path in @($paths | Where-Object { $_ } | Select-Object -Unique)) {
                $depth = Test-ControlChain $path
                if ($depth -ge 0) { [void]$references.Add([ordered]@{ vm = $machine.Name; vmId = [string]$machine.Id; disk = $path; relation = $(if ($depth -eq 0) { 'attached' } else { 'differencingParent' }) }) }
            }
            foreach ($dvd in @(Get-VMDvdDrive -VM $machine)) {
                $same = $false
                try { $same = $dvd.Path -and [string]::Equals([System.IO.Path]::GetFullPath([string]$dvd.Path), $target, [System.StringComparison]::OrdinalIgnoreCase) } catch { }
                if ($same) { [void]$references.Add([ordered]@{ vm = $machine.Name; vmId = [string]$machine.Id; disk = [string]$dvd.Path; relation = 'attachedDvd' }) }
            }
        }
        foreach ($child in $children) {
            $depth = Test-ControlChain $child
            if ($depth -gt 0) { [void]$references.Add([ordered]@{ vm = $null; vmId = $null; disk = $child; relation = 'differencingParent' }) }
        }
        """;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true, MaxAutomaticRedirections = 10, AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30), PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HyperVControl/" + Program.Version);
        return client;
    }

    private ImageDownloader Downloader => new(Http, StallTimeout);

    /// <summary>The host's default VHD and VM folders, read once through one script and cached.</summary>
    private async Task<HostPaths> HostPathsAsync(CancellationToken ct)
    {
        if (hostPaths is { } known) return known;
        await hostGate.WaitAsync(ct);
        try
        {
            if (hostPaths is { } again) return again;
            var data = await Runner.InvokeAsync(new HyperVScript().Returns("(Get-VMHost | Select-Object VirtualHardDiskPath, VirtualMachinePath)"), ct);
            var vhd = data.TryGetProperty("VirtualHardDiskPath", out var v) ? v.GetString() : null;
            var vm = data.TryGetProperty("VirtualMachinePath", out var m) ? m.GetString() : null;
            if (string.IsNullOrWhiteSpace(vhd) || string.IsNullOrWhiteSpace(vm))
                throw new HyperVOperationException("Get-VMHost returned no default virtual hard disk or virtual machine path.", "InvalidResponse");
            return hostPaths = new(vhd, vm);
        }
        finally { hostGate.Release(); }
    }

    /// <summary>The library root: <see cref="RootVariable"/> when set, else <c>&lt;VirtualHardDiskPath&gt;\HyperVControl\Images</c>.</summary>
    internal async Task<ImageLibraryStore> StoreAsync(CancellationToken ct)
    {
        var configured = Environment.GetEnvironmentVariable(RootVariable)?.Trim();
        string root;
        if (!string.IsNullOrEmpty(configured))
        {
            if (!Path.IsPathFullyQualified(configured) || configured.Split('\\', '/').Contains(".."))
                throw new ArgumentException($"{RootVariable} must be an absolute path without '..' components.");
            root = configured;
        }
        else root = Path.Combine((await HostPathsAsync(ct)).VirtualHardDiskPath, "HyperVControl", "Images");
        Directory.CreateDirectory(root);
        return new ImageLibraryStore(root);
    }

    // ---- list and catalogs ----------------------------------------------------------------------------

    /// <summary>Catalog images and library entries with warnings and a recommendation. Reads feeds only; never touches disk URLs.</summary>
    public async Task<object> ListAsync(CancellationToken ct)
    {
        var store = await StoreAsync(ct);
        var catalogs = await store.CatalogsAsync(ct);
        var library = await store.LibraryAsync(ct);
        var loads = await Task.WhenAll(catalogs.Catalogs.Select(async source =>
        {
            if (!source.Enabled) return (Source: source, Feed: (CatalogFeed?)null, Error: (string?)null);
            try { return (Source: source, Feed: await LoadFeedAsync(source, ct), Error: null); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return (Source: source, Feed: null, Error: ex.Message); }
        }));
        var images = loads.Where(l => l.Feed is not null).SelectMany(l => l.Feed!.Images).ToList();
        var recommendation = Recommend(images, library);
        return new
        {
            root = store.Root,
            freeBytes = ImageFiles.FreeBytes(store.Root),
            recommended = recommendation.Summary,
            library = library.Entries.Select(e => ProjectEntry(e, ReferenceEquals(e, recommendation.Deploy))).ToList(),
            catalogs = loads.Select(l => new
            {
                l.Source.Name, l.Source.Location, l.Source.Enabled, l.Source.BuiltIn,
                status = !l.Source.Enabled ? "disabled" : l.Feed is not null ? "ok" : "error",
                images = l.Feed?.Images.Count, hidden = l.Feed?.Hidden, skipped = l.Feed?.Skipped, error = l.Error
            }).ToList(),
            images = images.Select(i => ProjectImage(i, library, recommendation)).ToList(),
            cache = CacheFiles(store)
        };
    }

    public async Task<object> CatalogsAsync(string action, string? name, string? location, CancellationToken ct)
    {
        var store = await StoreAsync(ct);
        switch (action)
        {
            case "list":
                return new { file = store.CatalogsFile, catalogs = (await store.CatalogsAsync(ct)).Catalogs };
            case "add":
            {
                if (string.IsNullOrWhiteSpace(location)) throw new ArgumentException("location required: an http(s) URL, a file: URI, or an absolute local or UNC path to a gallery JSON.");
                location = location.Trim();
                var uri = ImageCatalogFeed.ParseLocation(location);
                var index = await store.CatalogsAsync(ct);
                var existing = index.Catalogs.FirstOrDefault(c => SameText(c.Location, location));
                var chosen = name?.Trim() is { Length: > 0 } given ? given : existing?.Name ?? DeriveCatalogName(uri, index);
                if (existing is null && index.Catalogs.Any(c => SameText(c.Name, chosen)))
                    throw new HyperVOperationException($"A catalog named '{chosen}' already exists; pass another catalog name.", "AlreadyExists");
                // Read and parse it now, so a bad location fails here instead of at the next list.
                var feed = ImageCatalogFeed.Parse(chosen, ImageCatalogFeed.Decode(await ReadLocationAsync(location, ct)), HostVersion, Clock());
                var saved = await store.UpdateCatalogsAsync(current =>
                {
                    var match = current.Catalogs.FirstOrDefault(c => SameText(c.Location, location));
                    if (match is null)
                    {
                        var added = new ImageCatalogSource(chosen, location);
                        return (current with { Catalogs = [.. current.Catalogs, added] }, added);
                    }
                    var enabled = match with { Enabled = true };
                    return (current with { Catalogs = current.Catalogs.Select(c => ReferenceEquals(c, match) ? enabled : c).ToList() }, enabled);
                }, ct);
                return new
                {
                    added = existing is null, reenabled = existing is { Enabled: false }, catalog = saved,
                    images = feed.Images.Select(i => new { i.Name, i.Version, i.Fetchable, i.Warnings }).ToList(), hidden = feed.Hidden, skipped = feed.Skipped
                };
            }
            case "remove":
            {
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(location)) throw new ArgumentException("catalog (its name) or location required.");
                var target = await store.UpdateCatalogsAsync(current =>
                {
                    var match = current.Catalogs.FirstOrDefault(c => (name is not null && SameText(c.Name, name.Trim())) || (location is not null && SameText(c.Location, location.Trim())))
                        ?? throw new HyperVOperationException($"No catalog named '{name ?? location}'. Use catalogAction=list.", "NotFound");
                    var catalogs = match.BuiltIn
                        ? current.Catalogs.Select(c => ReferenceEquals(c, match) ? c with { Enabled = false } : c).ToList()
                        : current.Catalogs.Where(c => !ReferenceEquals(c, match)).ToList();
                    return (current with { Catalogs = catalogs }, match);
                }, ct);
                return new
                {
                    catalog = target.Name, removed = !target.BuiltIn, disabled = target.BuiltIn,
                    note = target.BuiltIn ? "The built-in Microsoft feed is disabled, not deleted; add its location again to re-enable it." : null
                };
            }
            default:
                throw new ArgumentException("catalogAction must be list, add or remove.");
        }
    }

    private async Task<CatalogFeed> LoadFeedAsync(ImageCatalogSource source, CancellationToken ct)
    {
        var key = source.Name + "|" + source.Location;
        if (feeds.TryGetValue(key, out var cached) && Clock() - cached.At < FeedLifetime) return cached.Feed;
        var feed = ImageCatalogFeed.Parse(source.Name, ImageCatalogFeed.Decode(await ReadLocationAsync(source.Location, ct)), HostVersion, Clock());
        feeds[key] = (Clock(), feed);
        return feed;
    }

    /// <summary>Reads a catalog from an http(s) URL, a file: URI, or a local or UNC path (16 MB at most).</summary>
    internal static async Task<byte[]> ReadLocationAsync(string location, CancellationToken ct)
    {
        var uri = ImageCatalogFeed.ParseLocation(location);
        if (uri.IsFile)
        {
            var file = new FileInfo(uri.LocalPath);
            if (!file.Exists) throw new HyperVOperationException($"Catalog file not found: {file.FullName}.", "NotFound");
            if (file.Length > MaxFeedBytes) throw new InvalidDataException($"Catalog {file.FullName} is larger than 16 MB.");
            return await File.ReadAllBytesAsync(file.FullName, ct);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new HyperVOperationException($"Catalog {uri} returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.", "DownloadFailed");
            if (response.Content.Headers.ContentLength > MaxFeedBytes) throw new InvalidDataException($"Catalog {uri} is larger than 16 MB.");
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            return bytes.Length <= MaxFeedBytes ? bytes : throw new InvalidDataException($"Catalog {uri} is larger than 16 MB.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HyperVOperationException($"Catalog {uri} did not answer within 60 s.", "DownloadFailed");
        }
        catch (HttpRequestException ex)
        {
            throw new HyperVOperationException($"Cannot read catalog {uri}: {ex.Message}", "DownloadFailed");
        }
    }

    private static string DeriveCatalogName(Uri uri, ImageCatalogIndex index)
    {
        var stem = uri.IsFile ? Path.GetFileNameWithoutExtension(uri.LocalPath) : uri.Host + "-" + Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        var name = ImageFiles.SafeName(stem).ToLowerInvariant();
        var candidate = name;
        for (var n = 2; index.Catalogs.Any(c => SameText(c.Name, candidate)); n++) candidate = name + "-" + n;
        return candidate;
    }

    private async Task<(CatalogImage Image, ImageCatalogSource Source)> FindImageAsync(ImageLibraryStore store, string imageName, string? catalog, CancellationToken ct)
    {
        var sources = (await store.CatalogsAsync(ct)).Catalogs.Where(c => c.Enabled && (catalog is null || SameText(c.Name, catalog))).ToList();
        if (catalog is not null && sources.Count == 0) throw new HyperVOperationException($"No enabled catalog named '{catalog}'.", "NotFound");
        var matches = new List<(CatalogImage Image, ImageCatalogSource Source)>();
        var notes = new List<string>();
        foreach (var source in sources)
        {
            try
            {
                var feed = await LoadFeedAsync(source, ct);
                matches.AddRange(feed.Images.Where(i => SameText(i.Name, imageName)).Select(i => (i, source)));
                if (feed.Hidden > 0) notes.Add($"{feed.Hidden} image(s) in '{source.Name}' are hidden by their host version limits");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { notes.Add($"catalog '{source.Name}' is unreadable: {ex.Message}"); }
        }
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new HyperVOperationException($"No catalog image named '{imageName}'{(notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : "")}. "
                + "Call hyperv_image list for exact names.", "NotFound"),
            _ => throw new HyperVOperationException($"'{imageName}' is in catalogs {string.Join(", ", matches.Select(m => m.Source.Name))}; pass catalog.", "AmbiguousName")
        };
    }

    private static Recommendation Recommend(IReadOnlyList<CatalogImage> images, ImageLibraryIndex library)
    {
        var usable = images.Where(i => i.Fetchable && !i.Expired && !i.EndOfLife).ToList();
        CatalogImage? Pick(string family) => usable.Where(i => i.OsFamily == family)
            .OrderByDescending(i => i.LastUpdated ?? DateTimeOffset.MinValue).ThenByDescending(i => i.Version, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        var linux = Pick("linux");
        var windows = Pick("windows");
        var deploy = library.Entries.Where(e => e.Format == "vhdx" && !e.Expired && File.Exists(e.Path))
            .OrderByDescending(e => e.Kind == "template").ThenByDescending(e => e.RegisteredAt).FirstOrDefault();
        var hints = new List<string>();
        if (deploy is not null) hints.Add($"Deploy library {deploy.Kind} '{deploy.Name}' with action=deploy; a linked clone is ready in seconds.");
        if (linux is not null) hints.Add($"For Linux, fetch '{linux.Name}' from catalog '{linux.Catalog}' once, then deploy it.");
        if (windows is not null) hints.Add($"For Windows, fetch '{windows.Name}' from catalog '{windows.Catalog}' once, then deploy it.");
        else hints.Add("No catalog offers a current Windows image"
            + (images.Any(i => i.OsFamily == "windows" && i.Expired) ? " (its Windows evaluation images have expired)" : "")
            + ": build a golden image from a Windows ISO, or register your own VHDX with action=register.");
        return new(linux, windows, deploy, new
        {
            deploy = deploy?.Name,
            linux = linux is null ? null : new { linux.Catalog, linux.Name },
            windows = windows is null ? null : new { windows.Catalog, windows.Name },
            hint = string.Join(" ", hints)
        });
    }

    private static object ProjectImage(CatalogImage image, ImageLibraryIndex library, Recommendation recommendation) => new
    {
        image.Catalog, image.Name, image.Publisher, image.Version, image.LastUpdated, image.OsFamily, image.Description,
        disk = new { uri = image.DiskUri, type = image.DiskExtension.TrimStart('.'), sha256 = image.Sha256, image.ArchiveRelativePath },
        config = new { image.SecureBoot, image.EnhancedSessionTransportType },
        image.Fetchable, image.Expired, image.Stale, image.EndOfLife,
        recommended = ReferenceEquals(image, recommendation.Linux) || ReferenceEquals(image, recommendation.Windows),
        inLibrary = image.Sha256 is null ? null : library.Entries.FirstOrDefault(e => SameText(e.Source.Sha256, image.Sha256))?.Name,
        image.Warnings
    };

    internal static object ProjectEntry(ImageLibraryEntry entry, bool recommended = false)
    {
        var exists = File.Exists(entry.Path);
        var warnings = new List<string>(entry.Warnings ?? []);
        if (!exists) warnings.Add($"The image file is missing: {entry.Path}. Remove the entry or restore the file.");
        return new
        {
            entry.Name, entry.Kind, entry.OsFamily, entry.Path, entry.Format, exists,
            fileBytes = exists ? new FileInfo(entry.Path).Length : (long?)null,
            entry.ReadOnly, entry.Expired, entry.Config, entry.Source, entry.Sizes, entry.RegisteredAt, entry.Notes, entry.ArchivePath,
            deployments = entry.Deployments ?? [], recommended, warnings
        };
    }

    private static IReadOnlyList<object> CacheFiles(ImageLibraryStore store) => !Directory.Exists(store.CacheDirectory) ? [] :
        new DirectoryInfo(store.CacheDirectory).GetFiles()
            .Select(f => (object)new { f.Name, bytes = f.Length, partial = f.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase), modified = f.LastWriteTimeUtc })
            .ToList();

    // ---- fetch ----------------------------------------------------------------------------------------

    /// <summary>
    /// Starts (or joins) the fetch job for one catalog image and optionally waits for it. Returns the job snapshot,
    /// an <c>ok:false</c> envelope around it when it failed within the wait, or the existing entry when the image
    /// is already in the library.
    /// </summary>
    public async Task<object> FetchAsync(string? imageName, string? catalog, bool keepArchive, int waitSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imageName)) throw new ArgumentException("imageName required: a catalog image name from hyperv_image list.");
        if (waitSeconds is < 0 or > 3600) throw new ArgumentException("waitSeconds must be between 0 and 3600.");
        var store = await StoreAsync(ct);
        var (image, source) = await FindImageAsync(store, imageName.Trim(), catalog?.Trim(), ct);
        if (image.Sha256 is null)
            throw new HyperVOperationException($"'{image.Name}' has no valid SHA-256 in catalog '{image.Catalog}'; unverifiable images are never fetched.", "UnsupportedImage");
        if (!image.Fetchable)
            throw new HyperVOperationException($"'{image.Name}' cannot be fetched: {string.Join(" ", image.Warnings)}", "UnsupportedImage");
        JobInfo job;
        await fetchGate.WaitAsync(ct);
        try
        {
            var library = await store.LibraryAsync(ct);
            var same = library.Entries.FirstOrDefault(e => SameText(e.Source.Sha256, image.Sha256));
            if (same is not null && File.Exists(same.Path))
                return new { alreadyInLibrary = true, entry = ProjectEntry(same), next = $"Deploy it with action=deploy imageName='{same.Name}'." };
            if (library.Find(image.Name) is { } named)
                throw new HyperVOperationException($"The library already has an entry named '{named.Name}'" + (File.Exists(named.Path) ? " with different content" : " whose file is missing")
                    + "; remove it first (action=remove).", "AlreadyExists");
            if (activeFetches.TryGetValue(image.Sha256, out var activeId) && Jobs.Get(activeId) is { Status: JobStatus.Queued or JobStatus.Running } active)
                return new { alreadyRunning = true, job = active };
            job = Jobs.Start("fetch " + image.Name, (progress, token) => RunFetchAsync(store, image, source.Location, keepArchive, progress, token));
            activeFetches[image.Sha256] = job.Id;
        }
        finally { fetchGate.Release(); }
        return waitSeconds == 0 ? job : JobOutcome(await Jobs.WaitAsync(job.Id, TimeSpan.FromSeconds(waitSeconds), ct));
    }

    /// <summary>A job snapshot, wrapped in the failure envelope when the job failed or was cancelled.</summary>
    internal static object JobOutcome(JobInfo job) => job.Status is JobStatus.Failed or JobStatus.Cancelled
        ? new { ok = false, error = job.Error, errorType = job.ErrorType, job }
        : job;

    private async Task<object?> RunFetchAsync(ImageLibraryStore store, CatalogImage image, string catalogLocation, bool keepArchive, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var sha = image.Sha256!;
        var report = Throttle(progress);
        string? partial = null;          // a partial download this job owns
        string? downloaded = null;       // a complete, verified archive this job downloaded
        var outputs = new List<string>(); // extraction or copy output this job owns
        try
        {
            Directory.CreateDirectory(store.CacheDirectory);
            var uri = ImageCatalogFeed.ParseLocation(image.DiskUri);
            string source, extension;
            long downloadedBytes = 0, resumedFrom = 0;
            bool inCache;
            if (uri.IsFile)
            {
                // A local or UNC disk is verified in place and never copied to the cache.
                source = uri.LocalPath;
                extension = Path.GetExtension(source).ToLowerInvariant();
                RequireDiskType(extension, image);
                if (!File.Exists(source)) throw new HyperVOperationException($"The catalog's disk file does not exist: {source}.", "NotFound");
                var length = new FileInfo(source).Length;
                var actual = await ImageFiles.Sha256Async(source, done => report("verifying " + Path.GetFileName(source), done, length), ct);
                if (!SameText(actual, sha))
                    throw new HyperVOperationException($"SHA-256 mismatch: the catalog lists {sha} but {source} hashes to {actual}. The source file was left untouched.", "HashMismatch");
                inCache = false;
            }
            else
            {
                report("resolving " + uri.Host, 0, null);
                var probe = await Downloader.ProbeAsync(uri, ct);
                extension = probe.Extension;
                RequireDiskType(extension, image);
                var cached = Path.Combine(store.CacheDirectory, sha + extension);
                if (File.Exists(cached) && SameText(await ImageFiles.Sha256Async(cached, done => report("verifying the cached download", done, new FileInfo(cached).Length), ct), sha))
                    source = cached;
                else
                {
                    ImageFiles.TryDelete(cached);
                    partial = cached + ".partial";
                    var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                    long extract = 0;
                    if (extension == ".zip" && probe.ZipEntries is { } entries)
                        extract = (entries.FirstOrDefault(e => ImageFiles.SameEntry(e.FullName, image.ArchiveRelativePath!))
                            ?? throw new HyperVOperationException($"The zip has no entry '{image.ArchiveRelativePath}' (entries: {string.Join(", ", entries.Take(20).Select(e => e.FullName))}); nothing was downloaded.", "UnsupportedImage")).UncompressedSize;
                    if (probe.Length is long total)
                        ImageFiles.EnsureSpace(store.Root, Math.Max(0, total - have) + extract, $"downloading {image.Name}" + (extract > 0 ? " and extracting it" : ""));
                    var result = await Downloader.DownloadAsync(probe.FinalUri, partial, probe.Length, sha, report, ct);
                    downloadedBytes = result.Downloaded;
                    resumedFrom = result.ResumedFrom;
                    File.Move(partial, cached, overwrite: true);
                    partial = null;
                    downloaded = source = cached;
                }
                inCache = true;
            }
            if (!ImageFiles.HasMagic(source, extension))
            {
                if (downloaded is not null) { ImageFiles.TryDelete(downloaded); downloaded = null; }
                throw new HyperVOperationException($"The {(inCache ? "download" : "file")} is not a valid {extension} (its signature does not match)" + (inCache ? "; it was deleted." : "."), "UnsupportedImage");
            }

            string target;
            if (extension == ".zip")
            {
                var entryExtension = Path.GetExtension(image.ArchiveRelativePath!).ToLowerInvariant();
                target = UniqueLibraryPath(store.Root, image.Name, entryExtension);
                var temporary = target + ".partial";
                outputs.Add(temporary);
                await ImageFiles.ExtractAsync(source, image.ArchiveRelativePath!, temporary, (done, total) => report("extracting " + image.ArchiveRelativePath, done, total), ct);
                if (!ImageFiles.HasMagic(temporary, entryExtension))
                    throw new HyperVOperationException($"The extracted '{image.ArchiveRelativePath}' is not a valid {entryExtension} (its signature does not match); it was deleted.", "UnsupportedImage");
                File.Move(temporary, target);
                outputs.Add(target);
            }
            else if (inCache)
            {
                // A verified .vhdx or .iso download becomes the library file itself.
                target = UniqueLibraryPath(store.Root, image.Name, extension);
                File.Move(source, target);
                downloaded = null;
                outputs.Add(target);
            }
            else
            {
                target = UniqueLibraryPath(store.Root, image.Name, extension);
                var temporary = target + ".partial";
                outputs.Add(temporary);
                ImageFiles.EnsureSpace(store.Root, new FileInfo(source).Length, "copying " + image.Name);
                await ImageFiles.CopyAsync(source, temporary, (done, total) => report("copying " + Path.GetFileName(source), done, total), ct);
                File.Move(temporary, target);
                outputs.Add(target);
            }

            var archiveKept = extension == ".zip" && inCache && keepArchive ? source : null;
            var entry = new ImageLibraryEntry(image.Name, "image", image.OsFamily, target, Path.GetExtension(target).TrimStart('.').ToLowerInvariant(),
                new ImageSource(image.Catalog, catalogLocation, image.Name, image.Version, image.LastUpdated, image.DiskUri, sha, image.ArchiveRelativePath),
                new ImageSizes(new FileInfo(target).Length, extension == ".zip" && inCache ? new FileInfo(source).Length : null),
                new ImageConfig(image.SecureBoot, image.EnhancedSessionTransportType), Clock(),
                Expired: image.Expired, ArchivePath: archiveKept, Warnings: image.Warnings.Count > 0 ? image.Warnings : null);
            report("registering " + image.Name, 0, null);
            await store.UpdateLibraryAsync(index => index.Find(entry.Name) is null
                ? (index with { Entries = [.. index.Entries, entry] }, entry)
                : throw new HyperVOperationException($"A library entry named '{entry.Name}' appeared while fetching; the extracted file was deleted.", "AlreadyExists"), ct);
            outputs.Clear();
            if (extension == ".zip" && inCache && !keepArchive) ImageFiles.TryDelete(source);
            downloaded = null;
            progress.Report(new JobProgress(100, null, null, "done"));
            return new
            {
                entry = ProjectEntry(entry), archive = archiveKept, downloadedBytes, resumedFromBytes = resumedFrom, sha256 = sha,
                seconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
                next = $"Deploy it with action=deploy imageName='{entry.Name}' vmName=<new VM name>."
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation removes everything this job produced: the partial download and any extraction output.
            ImageFiles.TryDelete(partial);
            foreach (var output in outputs) ImageFiles.TryDelete(output);
            if (downloaded is not null && !keepArchive) ImageFiles.TryDelete(downloaded);
            throw;
        }
        catch (Exception ex)
        {
            // Failures never leave extraction output behind. An interrupted download keeps its partial file for
            // resume (a hash mismatch has already deleted it), and a verified archive is kept for a quick retry.
            foreach (var output in outputs) ImageFiles.TryDelete(output);
            if (downloaded is not null)
                throw new HyperVOperationException(ex.Message + $" The verified download is kept at {downloaded}; fetch again to reuse it.",
                    ex is HyperVOperationException known ? known.ErrorType : "ExtractionFailed");
            throw;
        }
        finally
        {
            activeFetches.TryRemove(sha, out _);
        }
    }

    private static void RequireDiskType(string extension, CatalogImage image)
    {
        if (!ImageCatalogFeed.DiskExtensions.Contains(extension))
            throw new HyperVOperationException($"'{image.Name}' resolves to a '{extension}' file; only a .zip containing a .vhdx, a .vhdx or an .iso is supported.", "UnsupportedImage");
        if (extension == ".zip" && image.ArchiveRelativePath is null)
            throw new HyperVOperationException($"'{image.Name}' is a .zip without archiveRelativePath.", "UnsupportedImage");
    }

    private static string UniqueLibraryPath(string root, string name, string extension)
    {
        var stem = ImageFiles.SafeName(name);
        var path = Path.Combine(root, stem + extension);
        for (var n = 2; File.Exists(path) || File.Exists(path + ".partial"); n++) path = Path.Combine(root, $"{stem}-{n}{extension}");
        return path;
    }

    /// <summary>Progress at most four times a second per stage, plus every stage change and completion.</summary>
    private static Action<string, long, long?> Throttle(IProgress<JobProgress> progress)
    {
        var clock = Stopwatch.StartNew();
        var last = TimeSpan.MinValue;
        string? stage = null;
        return (current, done, total) =>
        {
            if (current == stage && clock.Elapsed - last < TimeSpan.FromMilliseconds(250) && done != total) return;
            last = clock.Elapsed;
            stage = current;
            progress.Report(new JobProgress(total is > 0 ? Math.Round(100.0 * done / total.Value, 1) : null, done, total, current));
        };
    }

    // ---- register and remove --------------------------------------------------------------------------

    /// <summary>Adopts a local .vhdx or .iso in place. A template is made read-only, because it exists to be a differencing parent.</summary>
    public async Task<object> RegisterAsync(string? path, string? name, string? osFamily, string? kind, bool? secureBoot, string? notes, CancellationToken ct)
    {
        var file = ControlService.FullPath(path ?? throw new ArgumentException("path required: an absolute path to a local .vhdx or .iso."));
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100) throw new ArgumentException("name required (1-100 characters).");
        var entryName = name.Trim();
        var format = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".vhdx" => "vhdx",
            ".iso" => "iso",
            _ => throw new ArgumentException("register accepts a .vhdx or an .iso file.")
        };
        if (!File.Exists(file)) throw new HyperVOperationException($"File not found: {file}.", "NotFound");
        if (!ImageFiles.HasMagic(file, "." + format)) throw new HyperVOperationException($"{file} is not a valid .{format} (its signature does not match).", "UnsupportedImage");
        var entryKind = (kind ?? "image").Trim().ToLowerInvariant();
        if (!Kinds.Contains(entryKind)) throw new ArgumentException("kind must be image or template.");
        if (format == "iso" && entryKind == "template") throw new ArgumentException("An ISO is installation media; register it as kind=image.");
        var warnings = new List<string>();
        var family = osFamily?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(family))
        {
            family = ImageCatalogFeed.InferOsFamily(entryName + " " + Path.GetFileNameWithoutExtension(file), null, []);
            warnings.Add($"osFamily was not given; '{family}' was inferred from the name. Remove and register again with osFamily if that is wrong.");
        }
        else if (!OsFamilies.Contains(family)) throw new ArgumentException("osFamily must be windows, linux or other.");
        var store = await StoreAsync(ct);
        var entry = await store.UpdateLibraryAsync(index =>
        {
            if (index.Find(entryName) is not null) throw new HyperVOperationException($"A library entry named '{entryName}' already exists.", "AlreadyExists");
            if (index.Entries.FirstOrDefault(e => SamePath(e.Path, file)) is { } same) throw new HyperVOperationException($"{file} is already registered as '{same.Name}'.", "AlreadyExists");
            var template = entryKind == "template" && format == "vhdx";
            if (template) MakeReadOnly(file);
            var added = new ImageLibraryEntry(entryName, entryKind, family, file, format, new ImageSource(RegisteredFrom: file),
                new ImageSizes(new FileInfo(file).Length), new ImageConfig(secureBoot), Clock(), ReadOnly: template, Notes: notes);
            return (index with { Entries = [.. index.Entries, added] }, added);
        }, ct);
        return new { registered = true, entry = ProjectEntry(entry), warnings };
    }

    /// <summary>
    /// Removes a library entry (and with <paramref name="deleteFile"/> its file and kept archive). Refuses with
    /// <c>InUse</c> while any VM attaches the file, any disk chain (current, checkpoint or recorded child) has it as
    /// a differencing parent, or a VM's DVD drive holds it.
    /// </summary>
    public async Task<object> RemoveAsync(string? name, bool deleteFile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name required: the library entry to remove.");
        var store = await StoreAsync(ct);
        var entry = (await store.LibraryAsync(ct)).Find(name.Trim())
            ?? throw new HyperVOperationException($"No library entry named '{name}'. Call hyperv_image list.", "NotFound");
        var references = await ReferencesAsync(entry, ct);
        if (references.Count > 0)
        {
            var summary = string.Join("; ", references.Select(r =>
                $"{(r.TryGetProperty("vm", out var vm) && vm.ValueKind == JsonValueKind.String ? "VM '" + vm.GetString() + "'" : "an unattached disk")} "
                + $"{r.GetProperty("disk").GetString()} ({r.GetProperty("relation").GetString()})"));
            return new
            {
                ok = false, errorType = "InUse", references,
                error = $"'{entry.Name}' is in use: {summary}. Remove those VMs and child disks first; removing a differencing parent breaks its children."
            };
        }
        var deleted = new List<string>();
        if (deleteFile)
            foreach (var path in new[] { entry.Path, entry.ArchivePath })
                if (path is not null && File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    deleted.Add(path);
                }
        var removed = await store.UpdateLibraryAsync(index =>
        {
            var current = index.Find(entry.Name) ?? throw new HyperVOperationException($"No library entry named '{entry.Name}'.", "NotFound");
            return (index with { Entries = index.Entries.Where(e => !ReferenceEquals(e, current)).ToList() }, current);
        }, ct);
        return new
        {
            removed = removed.Name, deleted,
            kept = deleteFile ? null : removed.Path,
            note = !deleteFile && removed.ReadOnly ? "The file was kept with its read-only attribute." : null
        };
    }

    private async Task<List<JsonElement>> ReferencesAsync(ImageLibraryEntry entry, CancellationToken ct)
    {
        var children = (entry.Deployments ?? []).Select(d => d.DiskPath).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var script = new HyperVScript()
            .Line("$target = [System.IO.Path]::GetFullPath(" + PowerShellRunner.Literal(entry.Path) + ")")
            .Line("$children = @(" + string.Join(", ", children.Select(PowerShellRunner.Literal)) + ")")
            .Line(InUseGuard)
            .Returns("@($references)");
        var data = await Runner.InvokeAsync(script, ct, 300);
        return data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().Select(e => e.Clone()).ToList() : [];
    }

    private static void MakeReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) == 0) File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
    }

    // ---- deploy ---------------------------------------------------------------------------------------

    /// <summary>
    /// Creates a VM from a library entry: a differencing child of the entry (linked, the default) or a full copy under
    /// the VM's folder, the VM through <see cref="VmLifecycleService.CreateAsync"/>, then the lab defaults in one script:
    /// static memory, automatic checkpoints off, Standard checkpoints, Secure Boot per OS family (off when the image
    /// says so), HvSocket enhanced session for Linux, and a vTPM for Windows (after the template, which the vTPM locks).
    /// Any failure removes the half-created VM and its new disk.
    /// </summary>
    public async Task<object> DeployAsync(ImageDeployRequest request, VmLifecycleService lifecycle, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ImageName)) throw new ArgumentException("imageName required: a library entry name from hyperv_image list.");
        var vmName = request.VmName?.Trim();
        if (string.IsNullOrEmpty(vmName) || vmName.Length > 100) throw new ArgumentException("vmName required (1-100 characters).");
        if (request.MemoryMb is < 32 or > 1048576 || request.Processors is < 1 or > 2048) throw new ArgumentException("Invalid memoryMb or processors.");
        var store = await StoreAsync(ct);
        var entry = (await store.LibraryAsync(ct)).Find(request.ImageName.Trim())
            ?? throw new HyperVOperationException($"No library entry named '{request.ImageName}'. Fetch or register it first; hyperv_image list shows the library.", "NotFound");
        if (entry.Format != "vhdx")
            throw new ArgumentException($"'{entry.Name}' is an ISO (installation media), not a bootable disk. Create a VM with a new disk and attach the ISO instead.");
        if (!File.Exists(entry.Path)) throw new HyperVOperationException($"The file of '{entry.Name}' is missing: {entry.Path}.", "NotFound");
        var vmRoot = request.VmPath is null ? (await HostPathsAsync(ct)).VirtualMachinePath : ControlService.FullPath(request.VmPath);
        var folder = Path.Combine(vmRoot, ImageFiles.SafeDirectoryName(vmName));
        var directory = Path.Combine(folder, "Virtual Hard Disks");
        var disk = Path.Combine(directory, ImageFiles.SafeDirectoryName(vmName) + ".vhdx");
        if (File.Exists(disk)) throw new HyperVOperationException($"{disk} already exists; choose another vmName or vmPath.", "AlreadyExists");
        var warnings = new List<string>(entry.Warnings ?? []);
        var createdFolder = !Directory.Exists(folder);
        Directory.CreateDirectory(directory);
        var ownsDisk = false;
        string? vmId = null;
        try
        {
            // 1. One script: refuse a duplicate VM name, resolve the switch, and create the differencing child.
            var prepare = new HyperVScript()
                .Line("$name = " + PowerShellRunner.Literal(vmName))
                .Line("if (@(Get-VM | Where-Object { $_.Name -eq $name }).Count -gt 0) { Stop-Control 'InvalidArgument' (\"A VM named '\" + $name + \"' already exists. Choose another vmName.\") }");
            if (request.SwitchName is { } requested)
                prepare.Line("$switchName = " + PowerShellRunner.Literal(requested))
                    .Line("if (@(Get-VMSwitch | Where-Object { $_.Name -eq $switchName }).Count -eq 0) { Stop-Control 'NotFound' (\"Switch '\" + $switchName + \"' not found. Call hyperv_network list.\") }");
            else
                prepare.Line("$switchName = $null")
                    .Line("try { $default = Get-VMSwitch -Id " + PsValue.Of(Guid.Parse(DefaultSwitchId)).Text + " -ErrorAction Stop; if ($default) { $switchName = [string]@($default)[0].Name } } catch { }");
            if (request.Linked)
                prepare.Cmd("$child = New-VHD", ("Path", PsValue.Of(disk)), ("ParentPath", PsValue.Of(entry.Path)), ("Differencing", PsValue.Of(true)));
            ownsDisk = request.Linked;
            var prepared = await Runner.InvokeAsync(prepare.Returns("@{ switchName = $switchName }"), ct, 300);
            var switchName = prepared.TryGetProperty("switchName", out var chosen) && chosen.ValueKind == JsonValueKind.String ? chosen.GetString() : null;
            if (switchName is null) warnings.Add("No switchName was given and this host has no Default Switch, so the VM's network adapter is not connected.");
            if (request.Linked) entry = await ProtectParentAsync(store, entry, ct);
            else
            {
                ImageFiles.EnsureSpace(directory, new FileInfo(entry.Path).Length, "copying " + entry.Name);
                ownsDisk = true;
                await ImageFiles.CopyAsync(entry.Path, disk, null, ct);
            }

            // 2. The VM itself; this is the only call into the lifecycle service.
            var created = await lifecycle.CreateAsync(vmName, vmRoot, disk, 2, request.MemoryMb, request.Processors, 0, switchName, ct);
            vmId = created.GetProperty("Id").GetString() ?? throw new HyperVOperationException("New-VM returned no VM ID.", "InvalidResponse");

            // 3. Lab defaults, then the optional start.
            var configured = await Runner.InvokeAsync(LabDefaults(vmId, entry, request, warnings), ct, 300);
            if (configured.TryGetProperty("startError", out var startError) && startError.ValueKind == JsonValueKind.String)
                warnings.Add("The VM was created and configured but did not start: " + startError.GetString());
            var deployment = new ImageDeployment(vmName, vmId, disk, request.Linked, Clock());
            await store.UpdateLibraryAsync(index => index.Find(entry.Name) is { } current
                ? (Replace(index, current, current with { Deployments = [.. current.Deployments ?? [], deployment] }), true)
                : (index, false), ct);
            return new
            {
                vm = configured.GetProperty("vm"),
                disk = new { path = disk, type = request.Linked ? "differencing" : "copy", parentPath = request.Linked ? entry.Path : null },
                parent = new { entry.Name, entry.Path, entry.Kind, entry.OsFamily, entry.ReadOnly },
                switchName,
                firmware = configured.TryGetProperty("firmware", out var firmware) ? firmware : default,
                security = configured.TryGetProperty("security", out var security) ? security : default,
                started = request.Start && !(startError.ValueKind == JsonValueKind.String),
                warnings
            };
        }
        catch
        {
            await RollbackAsync(vmId, ownsDisk ? disk : null, createdFolder ? folder : directory);
            throw;
        }
    }

    private static HyperVScript LabDefaults(string vmId, ImageLibraryEntry entry, ImageDeployRequest request, List<string> warnings)
    {
        var vm = PsValue.Raw("$vm");
        var windows = entry.OsFamily == "windows";
        var linux = entry.OsFamily == "linux";
        var transport = entry.Config.EnhancedSessionTransportType ?? (linux ? "HvSocket" : null);
        var script = new HyperVScript().Vm(vmId).RequireOff()
            .Cmd("Set-VM", ("VM", vm), ("StaticMemory", PsValue.Of(true)), ("AutomaticCheckpointsEnabled", PsValue.Of(false)),
                ("CheckpointType", PsValue.Token("Standard", "Standard")),
                ("EnhancedSessionTransportType", transport is null ? null : PsValue.Token(transport, "HvSocket", "VMBus")),
                ("Notes", request.Notes is null ? null : PsValue.Of(request.Notes)));
        // Secure Boot follows the image (off when it says so), else the template that matches the OS family.
        var template = entry.Config.SecureBoot == false ? null
            : windows ? "MicrosoftWindows"
            : linux || entry.Config.SecureBoot == true ? "MicrosoftUEFICertificateAuthority"
            : null;
        if (template is null)
        {
            script.Cmd("Set-VMFirmware", ("VM", vm), ("EnableSecureBoot", PsValue.Token("Off", "On", "Off")));
            if (entry.Config.SecureBoot is null)
                warnings.Add("Secure Boot is off because the image's OS family is 'other'; enable it with hyperv_firmware if the guest supports it.");
        }
        else
            script.Cmd("Set-VMFirmware", ("VM", vm), ("EnableSecureBoot", PsValue.Token("On", "On", "Off")),
                ("SecureBootTemplate", PsValue.Token(template, "MicrosoftWindows", "MicrosoftUEFICertificateAuthority")));
        // The vTPM comes after the template: initializing it locks the Secure Boot template.
        if (windows) script.Cmd("Set-VMKeyProtector", ("VM", vm), ("NewLocalKeyProtector", PsValue.Of(true))).Cmd("Enable-VMTPM", ("VM", vm));
        script.Line("$startError = $null");
        if (request.Start) script.Line("try { Start-VM -VM $vm } catch { $startError = [string]$_.Exception.Message }");
        return script.Line("$vm = Get-VM -Id $vm.Id -ErrorAction Stop").Returns(
            "@{ vm = " + VmProjection
            + "; firmware = (Get-VMFirmware -VM $vm | Select-Object @{Name='SecureBoot';Expression={[string]$_.SecureBoot}},SecureBootTemplate)"
            + "; security = (Get-VMSecurity -VM $vm | Select-Object TpmEnabled); startError = $startError }");
    }

    private static async Task<ImageLibraryEntry> ProtectParentAsync(ImageLibraryStore store, ImageLibraryEntry entry, CancellationToken ct) =>
        await store.UpdateLibraryAsync(index =>
        {
            var current = index.Find(entry.Name) ?? entry;
            MakeReadOnly(current.Path);
            if (current.ReadOnly || !index.Entries.Contains(current)) return (index, current with { ReadOnly = true });
            var updated = current with { ReadOnly = true };
            return (Replace(index, current, updated), updated);
        }, ct);

    /// <summary>Best effort: removes a half-created VM, the disk this deploy created and the folders it left empty.</summary>
    private async Task RollbackAsync(string? vmId, string? disk, string folder)
    {
        if (vmId is not null && Guid.TryParse(vmId, out var id))
        {
            try
            {
                await Runner.InvokeAsync(new HyperVScript()
                    .Line("$vm = $null; try { $vm = Get-VM -Id " + PsValue.Of(id).Text + " -ErrorAction Stop } catch { }")
                    .Line("if ($vm) { if ([string]$vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force }; Remove-VM -VM $vm -Force }")
                    .Returns("$true"), CancellationToken.None, 120);
            }
            catch (Exception) { /* the original failure is the one reported */ }
        }
        ImageFiles.TryDelete(disk);
        DeleteEmptyFolders(folder);
    }

    private static void DeleteEmptyFolders(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var child in Directory.GetDirectories(folder)) DeleteEmptyFolders(child);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static ImageLibraryIndex Replace(ImageLibraryIndex index, ImageLibraryEntry current, ImageLibraryEntry updated) =>
        index with { Entries = index.Entries.Select(e => ReferenceEquals(e, current) ? updated : e).ToList() };

    private static bool SameText(string? a, string? b) => a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
