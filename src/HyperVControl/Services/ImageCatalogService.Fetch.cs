using System.Diagnostics;

namespace HyperVControl.Services;

/// <summary>Fetch jobs: download with resume, verification, extraction into the library, and cleanup on cancel or failure.</summary>
internal sealed partial class ImageCatalogService
{
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
            // A quick create that is downloading this image is joined like a fetch job; its result is the VM it creates.
            if (activeFetches.TryGetValue(image.Sha256, out var activeId) && Jobs.Get(activeId) is { Status: JobStatus.Queued or JobStatus.Running } active)
                return new { alreadyRunning = true, job = active };
            job = Jobs.Start("fetch " + image.Name, async (progress, token) =>
            {
                var result = await RunFetchAsync(store, image, source.Location, keepArchive, progress, token);
                progress.Report(new JobProgress(100, null, null, "done"));
                return result;
            });
            activeFetches[image.Sha256] = job.Id;
        }
        finally { fetchGate.Release(); }
        return waitSeconds == 0 ? job : JobOutcome(await Jobs.WaitAsync(job.Id, TimeSpan.FromSeconds(waitSeconds), ct));
    }

    /// <summary>A job snapshot, wrapped in the failure envelope when the job failed or was cancelled.</summary>
    internal static object JobOutcome(JobInfo job) => job.Status is JobStatus.Failed or JobStatus.Cancelled
        ? new { ok = false, error = job.Error, errorType = job.ErrorType, job }
        : job;

    /// <summary>
    /// The fetch pipeline shared by fetch jobs and quick-create jobs: probe, download with resume, verify, extract, register.
    /// It reports progress up to registration; the caller reports completion. It always clears <c>activeFetches</c> for the image.
    /// </summary>
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
                ArchivePath: archiveKept, Warnings: image.Warnings.Count > 0 ? image.Warnings : null);
            report("registering " + image.Name, 0, null);
            await store.UpdateLibraryAsync(index => index.Find(entry.Name) is null
                ? (index with { Entries = [.. index.Entries, entry] }, entry)
                : throw new HyperVOperationException($"A library entry named '{entry.Name}' appeared while fetching; the extracted file was deleted.", "AlreadyExists"), ct);
            outputs.Clear();
            if (extension == ".zip" && inCache && !keepArchive) ImageFiles.TryDelete(source);
            downloaded = null;
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
}
