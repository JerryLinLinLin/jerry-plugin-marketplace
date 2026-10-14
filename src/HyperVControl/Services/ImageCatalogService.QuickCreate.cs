namespace HyperVControl.Services;

/// <summary>
/// The image a VM is created from. <see cref="Entry"/> is set when the library holds it (deployed at once);
/// otherwise <see cref="Image"/> is a catalog image that is fetched first. <see cref="Origin"/> says how it was chosen.
/// </summary>
internal sealed record ImageChoice(ImageLibraryEntry? Entry, CatalogImage? Image, ImageCatalogSource? Source, string Origin)
{
    public string Name => Image?.Name ?? Entry!.Name;
    public string? Catalog => Image?.Catalog ?? Entry?.Source.Catalog;
}

/// <summary>
/// The default image and one-call VM creation from an image: <c>hyperv_vm_create</c> with source=image deploys a library
/// image in seconds, or runs one job that fetches a catalog image and then deploys it.
/// </summary>
internal sealed partial class ImageCatalogService
{
    internal const string RecommendedWindowsOrigin = "recommended windows catalog image";
    internal const string LibraryFallbackOrigin = "newest windows library image (no catalog offers one)";

    private static string? ConfiguredDefault() => Environment.GetEnvironmentVariable(DefaultImageVariable)?.Trim() is { Length: > 0 } name ? name : null;

    /// <summary>
    /// The default image of <c>hyperv_vm_create</c>. <see cref="DefaultImageVariable"/>, when set, names a library entry or else a
    /// catalog image (exact name, case-insensitive). Otherwise it is the recommended Windows catalog image, the newest fetchable
    /// Windows image (Microsoft's "Windows 11 dev environment" in its Quick Create gallery), or, when no catalog offers one, the newest
    /// Windows image the library fetched from a catalog. A catalog image the library already holds (same SHA-256, else same name)
    /// resolves to that entry.
    /// </summary>
    public async Task<ImageChoice> ResolveDefaultImageAsync(string? catalog, CancellationToken ct)
    {
        var store = await StoreAsync(ct);
        var library = await store.LibraryAsync(ct);
        // A configured library entry needs no catalog read, so it works offline.
        if (ConfiguredDefault() is { } configured && library.Find(configured) is { } entry) return new(entry, null, null, DefaultImageVariable);
        var loads = await LoadCatalogsAsync(store, catalog, ct);
        return ChooseDefault(loads, library, Recommend(Images(loads), library).Windows);
    }

    /// <summary>An image the caller names: a library entry, else a catalog image (exact name, case-insensitive; <paramref name="catalog"/> disambiguates).</summary>
    public async Task<ImageChoice> ResolveImageAsync(string imageName, string? catalog, CancellationToken ct)
    {
        var store = await StoreAsync(ct);
        var library = await store.LibraryAsync(ct);
        if (library.Find(imageName) is { } entry) return new(entry, null, null, "imageName");
        var loads = await LoadCatalogsAsync(store, catalog, ct);
        return MatchImage(loads, imageName) is { } match
            ? new(InLibrary(match.Image, library), match.Image, match.Source, "imageName")
            : throw new HyperVOperationException($"No library entry or catalog image named '{imageName}'{CatalogNotes(loads)}. Call hyperv_image list for exact names.", "NotFound");
    }

    private static ImageChoice ChooseDefault(IReadOnlyList<CatalogLoad> loads, ImageLibraryIndex library, CatalogImage? recommendedWindows)
    {
        if (ConfiguredDefault() is { } configured)
        {
            if (library.Find(configured) is { } entry) return new(entry, null, null, DefaultImageVariable);
            if (MatchImage(loads, configured) is { } match) return new(InLibrary(match.Image, library), match.Image, match.Source, DefaultImageVariable);
            throw new HyperVOperationException($"{DefaultImageVariable} is '{configured}', but no library entry or catalog image has that exact name{CatalogNotes(loads)}. "
                + "Fix or clear the variable (hyperv_image list shows the names), or pass imageName.", "NotFound");
        }
        if (recommendedWindows is not null)
            return new(InLibrary(recommendedWindows, library), recommendedWindows, loads.First(l => SameText(l.Source.Name, recommendedWindows.Catalog)).Source, RecommendedWindowsOrigin);
        var fetched = library.Entries.Where(e => e.OsFamily == "windows" && e.Format == "vhdx" && e.Source.Sha256 is not null && File.Exists(e.Path))
            .OrderByDescending(e => e.Source.LastUpdated ?? DateTimeOffset.MinValue).ThenByDescending(e => e.RegisteredAt).FirstOrDefault();
        if (fetched is not null) return new(fetched, null, null, LibraryFallbackOrigin);
        throw new HyperVOperationException($"No default image: no enabled catalog offers a fetchable Windows image{CatalogNotes(loads)}, the library holds none fetched from a catalog, "
            + $"and {DefaultImageVariable} is not set. Set it to a library entry or catalog image name, pass imageName, or create the VM with source=new_disk or source=iso.", "NotFound");
    }

    /// <summary>The library entry holding a catalog image: the same verified SHA-256 (an existing file first), else the same name.</summary>
    private static ImageLibraryEntry? InLibrary(CatalogImage image, ImageLibraryIndex library) =>
        (image.Sha256 is null ? null : library.Entries.Where(e => SameText(e.Source.Sha256, image.Sha256)).OrderByDescending(e => File.Exists(e.Path)).FirstOrDefault())
        ?? library.Find(image.Name);

    /// <summary>The <c>defaultImage</c> block of <c>hyperv_image list</c>; an unresolvable default is reported, not thrown.</summary>
    private static object DefaultImageSummary(IReadOnlyList<CatalogLoad> loads, ImageLibraryIndex library, CatalogImage? recommendedWindows)
    {
        try
        {
            var choice = ChooseDefault(loads, library, recommendedWindows);
            return new { name = choice.Name, catalog = choice.Catalog, inLibrary = choice.Entry?.Name, source = choice.Origin };
        }
        catch (HyperVOperationException ex)
        {
            return new
            {
                name = (string?)null, catalog = (string?)null, inLibrary = (string?)null,
                source = ConfiguredDefault() is null ? RecommendedWindowsOrigin : DefaultImageVariable, error = ex.Message
            };
        }
    }

    /// <summary><c>hyperv_image deploy</c>: a library entry by name or, without one, the default image when the library already holds it.</summary>
    public async Task<object> DeployFromLibraryAsync(ImageDeployRequest request, string? catalog, VmLifecycleService lifecycle, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.ImageName)) return await DeployAsync(request, lifecycle, ct);
        if (string.IsNullOrWhiteSpace(request.VmName) || request.VmName.Trim().Length > 100) throw new ArgumentException("vmName required (1-100 characters).");
        var choice = await ResolveDefaultImageAsync(catalog?.Trim(), ct);
        return choice.Entry is { } entry
            ? await DeployAsync(request with { ImageName = entry.Name }, lifecycle, ct)
            : throw new HyperVOperationException($"The default image '{choice.Name}' (catalog '{choice.Catalog}') is not in the library yet. "
                + "hyperv_vm_create without source fetches it and creates the VM in one job; or run action=fetch first, then deploy.", "NotFound");
    }

    /// <summary>
    /// <c>hyperv_vm_create</c> with source=image. <paramref name="imageName"/> (a library entry, else a catalog image) or the default
    /// image. A library image is deployed in this call (a linked clone, seconds). A catalog image starts one job, "quick create
    /// &lt;vm&gt;", that fetches it (or waits for a running fetch of the same image instead of downloading it twice) and then deploys
    /// it; the job's result is the deploy result. The VM's name, switch and disk path are checked before any download starts.
    /// Returns the deploy result, the finished job (or its failure envelope), or the running job with the call that waits for it.
    /// </summary>
    public async Task<object> CreateVmAsync(string? imageName, string? catalog, ImageDeployRequest request, VmLifecycleService lifecycle, int waitSeconds, CancellationToken ct)
    {
        if (waitSeconds is < 0 or > 3600) throw new ArgumentException("waitSeconds must be between 0 and 3600.");
        var vmName = request.VmName?.Trim() ?? "";
        VmLifecycleService.ValidateNewVm(vmName, request.MemoryMb, request.Processors);
        if (request.VmPath is not null) ControlService.FullPath(request.VmPath);
        request = request with { VmName = vmName };
        catalog = string.IsNullOrWhiteSpace(catalog) ? null : catalog.Trim();
        var choice = string.IsNullOrWhiteSpace(imageName) ? await ResolveDefaultImageAsync(catalog, ct) : await ResolveImageAsync(imageName.Trim(), catalog, ct);
        if (choice.Entry is { } chosen) return await DeployAsync(request with { ImageName = chosen.Name }, lifecycle, ct);

        var image = choice.Image!;
        if (image.Sha256 is null)
            throw new HyperVOperationException($"'{image.Name}' has no valid SHA-256 in catalog '{image.Catalog}'; unverifiable images are never fetched.", "UnsupportedImage");
        if (!image.Fetchable)
            throw new HyperVOperationException($"'{image.Name}' cannot be fetched: {string.Join(" ", image.Warnings)}", "UnsupportedImage");
        // A download of many GB must never end in a refusal that was knowable up front.
        await PreflightDeployAsync(request, lifecycle, ct);

        var store = await StoreAsync(ct);
        ImageLibraryEntry? ready;
        JobInfo? job = null;
        await fetchGate.WaitAsync(ct);
        try
        {
            var library = await store.LibraryAsync(ct);
            ready = library.Entries.FirstOrDefault(e => SameText(e.Source.Sha256, image.Sha256) && File.Exists(e.Path));
            if (ready is null)
            {
                if (library.Find(image.Name) is { } named)
                    throw new HyperVOperationException($"The library already has an entry named '{named.Name}'" + (File.Exists(named.Path) ? " with different content" : " whose file is missing")
                        + "; remove it first (hyperv_image action=remove).", "AlreadyExists");
                var name = "quick create " + vmName;
                if (activeFetches.TryGetValue(image.Sha256, out var activeId) && Jobs.Get(activeId) is { Status: JobStatus.Queued or JobStatus.Running } active)
                    job = Jobs.Start(name, (progress, token) => JoinAndDeployAsync(store, image, active, request, lifecycle, progress, token));
                else
                {
                    var location = choice.Source!.Location;
                    job = Jobs.Start(name, async (progress, token) =>
                    {
                        await RunFetchAsync(store, image, location, false, progress, token);
                        return await DeployFetchedAsync(store, image, request, lifecycle, progress, token);
                    });
                    activeFetches[image.Sha256] = job.Id;
                }
            }
        }
        finally { fetchGate.Release(); }
        if (ready is not null) return await DeployAsync(request with { ImageName = ready.Name }, lifecycle, ct);

        var state = waitSeconds == 0 ? job! : await Jobs.WaitAsync(job!.Id, TimeSpan.FromSeconds(waitSeconds), ct);
        return state.Status is JobStatus.Queued or JobStatus.Running
            ? new
            {
                state.Id, state.Name, state.Status, state.StartedAt, state.FinishedAt, state.Progress, state.Result, state.Error, state.ErrorType,
                next = $"hyperv_jobs action=wait jobId={state.Id}"
            }
            : JobOutcome(state);
    }

    /// <summary>
    /// What would refuse the deploy and needs no image, checked before a fetch: the target disk, and the host's create checks
    /// (an existing VM name unless allowed, the switch), with the same messages as the create itself.
    /// </summary>
    private async Task PreflightDeployAsync(ImageDeployRequest request, VmLifecycleService lifecycle, CancellationToken ct)
    {
        var vmRoot = request.VmPath is null ? (await HostPathsAsync(ct)).VirtualMachinePath : ControlService.FullPath(request.VmPath);
        var (_, _, disk) = DeployPaths(vmRoot, request.VmName);
        if (File.Exists(disk)) throw new HyperVOperationException($"{disk} already exists; choose another vmName or vmPath.", "AlreadyExists");
        await lifecycle.CheckCreateAsync(request.VmName, request.SwitchName, request.AllowDuplicateName, ct);
    }

    /// <summary>Waits for another job that is fetching the same image, mirroring its progress, then deploys from the library.</summary>
    private async Task<object?> JoinAndDeployAsync(ImageLibraryStore store, CatalogImage image, JobInfo fetch, ImageDeployRequest request, VmLifecycleService lifecycle,
        IProgress<JobProgress> progress, CancellationToken ct)
    {
        var state = fetch;
        while (state.Status is JobStatus.Queued or JobStatus.Running)
        {
            progress.Report(state.Progress with { Message = $"job {fetch.Id} ({fetch.Name}) is fetching '{image.Name}': {state.Progress.Message ?? "queued"}" });
            state = await Jobs.WaitAsync(fetch.Id, TimeSpan.FromSeconds(1), ct);
        }
        if (await FetchedEntryAsync(store, image, ct) is null)
            throw new HyperVOperationException($"Job {fetch.Id} ({fetch.Name}) did not fetch '{image.Name}': "
                + (state.Error ?? "it ended " + state.Status.ToString().ToLowerInvariant()).TrimEnd('.') + ". Call hyperv_vm_create again to retry the download.",
                state.ErrorType ?? "FetchFailed");
        return await DeployFetchedAsync(store, image, request, lifecycle, progress, ct);
    }

    /// <summary>Deploys a freshly fetched image. A failure keeps the image in the library and says so, because a retry then takes seconds.</summary>
    private async Task<object?> DeployFetchedAsync(ImageLibraryStore store, CatalogImage image, ImageDeployRequest request, VmLifecycleService lifecycle,
        IProgress<JobProgress> progress, CancellationToken ct)
    {
        var entry = await FetchedEntryAsync(store, image, ct)
            ?? throw new HyperVOperationException($"'{image.Name}' was fetched, but no library entry holds it any more; call hyperv_image list.", "NotFound");
        progress.Report(new JobProgress(null, null, null, $"creating VM '{request.VmName}' from '{entry.Name}'"));
        object result;
        try { result = await DeployAsync(request with { ImageName = entry.Name }, lifecycle, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new HyperVOperationException($"'{image.Name}' is in the library as '{entry.Name}', but creating VM '{request.VmName}' failed: {ex.Message} "
                + "Fix the cause and call hyperv_vm_create again; it deploys from the library without downloading.",
                ex is HyperVOperationException known ? known.ErrorType : ex.GetType().Name);
        }
        progress.Report(new JobProgress(100, null, null, "done"));
        return result;
    }

    private static async Task<ImageLibraryEntry?> FetchedEntryAsync(ImageLibraryStore store, CatalogImage image, CancellationToken ct) =>
        (await store.LibraryAsync(ct)).Entries.FirstOrDefault(e => SameText(e.Source.Sha256, image.Sha256) && File.Exists(e.Path));
}
