using System.Collections.Concurrent;
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
internal sealed partial class ImageCatalogService(PowerShellRunner runner, JobRunner jobs)
{
    /// <summary>Overrides the library root (default <c>&lt;Get-VMHost VirtualHardDiskPath&gt;\HyperVControl\Images</c>).</summary>
    public const string RootVariable = "HYPERV_CONTROL_IMAGE_ROOT";
    /// <summary>Microsoft's fixed ID of the Default Switch; its name is localized.</summary>
    internal const string DefaultSwitchId = "c08cb7b8-9b3c-408e-8e30-5e16a3aeb444";
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

    private static ImageLibraryIndex Replace(ImageLibraryIndex index, ImageLibraryEntry current, ImageLibraryEntry updated) =>
        index with { Entries = index.Entries.Select(e => ReferenceEquals(e, current) ? updated : e).ToList() };

    private static bool SameText(string? a, string? b) => a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
