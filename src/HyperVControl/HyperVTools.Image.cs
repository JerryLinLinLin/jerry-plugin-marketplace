using System.ComponentModel;
using HyperVControl.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HyperVControl;

/// <summary>Image catalog, library and job tools (<c>hyperv_image</c>, <c>hyperv_jobs</c>): thin wrappers over
/// <c>ImageCatalogService</c> and <c>JobRunner</c>.</summary>
internal sealed partial class HyperVTools
{
    [McpServerTool(Name = "hyperv_jobs")]
    [Description("List, inspect, wait for or cancel background jobs (image fetches) owned by this MCP process; jobs end with it. action=list|status|wait|cancel; jobId for status/wait/cancel; waitSeconds (0-3600, default 30) bounds wait and how long cancel waits for cleanup. A job reports status (queued, running, succeeded, failed, cancelled), progress {percent, bytesDone, bytesTotal, message}, result, error and errorType. Cancelling a fetch deletes its partial download and extraction output; an interrupted download keeps its partial file and the next fetch resumes it.")]
    public Task<CallToolResult> JobsTool(string action = "list", string? jobId = null, int waitSeconds = 30, CancellationToken ct = default) => Run(async () =>
    {
        if (waitSeconds is < 0 or > 3600) throw new ArgumentException("waitSeconds must be between 0 and 3600.");
        string Id() => string.IsNullOrWhiteSpace(jobId) ? throw new ArgumentException("jobId required; call hyperv_jobs with action=list.") : jobId.Trim();
        switch (action)
        {
            case "list": return new { jobs = service.Jobs.List() };
            case "status": return service.Jobs.Get(Id()) ?? throw new ArgumentException($"Unknown job '{jobId}'. Jobs belong to the MCP process that started them; call hyperv_jobs with action=list.");
            case "wait": return await service.Jobs.WaitAsync(Id(), TimeSpan.FromSeconds(waitSeconds), ct);
            case "cancel":
                var id = Id();
                service.Jobs.Cancel(id);
                // Give the job a moment to observe the cancellation and delete its partial output.
                return await service.Jobs.WaitAsync(id, TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 1, 60)), ct);
            default: throw new ArgumentException("Use list, status, wait or cancel.");
        }
    });

    [McpServerTool(Name = "hyperv_image", Destructive = true, OpenWorld = true)]
    [Description("Image catalogs, the local image library and VM deployment from it. action=list: catalog images (Microsoft's Quick Create gallery plus custom catalogs) and library entries with warnings (expired evaluations, stale or unsupported releases, missing hashes) and a recommendation; never downloads a disk. fetch: imageName (catalog to disambiguate) runs a hyperv_jobs job that downloads with resume, verifies SHA-256 and magic bytes, extracts the .vhdx into the library and deletes the archive unless keepArchive; waitSeconds (0-3600) waits for it. register: adopt a local .vhdx/.iso at path as name (osFamily windows|linux|other, kind image|template, secureBoot, notes) without copying. remove: name, deleteFile; refused while a VM attaches the file or a disk uses it as a differencing parent. catalogs: catalogAction list|add|remove with location (http(s) URL, file, or UNC path to a gallery JSON) and catalog (name). deploy: imageName (library entry), vmName, linked (differencing child, default) or a full copy, memoryMb, processors, switchName (default: the Default Switch), vmPath, notes, start; applies lab defaults: static memory, automatic checkpoints off, Standard checkpoints, Secure Boot per OS family, HvSocket for Linux, vTPM for Windows. Library root: <host VHD path>\\HyperVControl\\Images or HYPERV_CONTROL_IMAGE_ROOT.")]
    public Task<CallToolResult> ImageTool(
        [Description("list | fetch | register | remove | catalogs | deploy")] string action = "list",
        [Description("fetch: catalog image name; deploy: library entry name")] string? imageName = null,
        [Description("fetch: catalog to search; catalogs add/remove: catalog name")] string? catalog = null,
        [Description("fetch: keep the downloaded zip in the cache after extraction")] bool keepArchive = false,
        [Description("fetch: seconds to wait for the job (0-3600); 0 returns the job at once")] int waitSeconds = 0,
        [Description("register: absolute path of a local .vhdx or .iso")] string? path = null,
        [Description("register: new library entry name; remove: library entry name")] string? name = null,
        [Description("register: windows | linux | other (inferred from the name when omitted)")] string? osFamily = null,
        [Description("register: image | template (templates are made read-only)")] string kind = "image",
        [Description("register: Secure Boot for deploys; omit to follow the OS family")] bool? secureBoot = null,
        [Description("register: entry notes; deploy: VM notes")] string? notes = null,
        [Description("remove: also delete the image file and any kept archive")] bool deleteFile = false,
        [Description("catalogs: list | add | remove")] string catalogAction = "list",
        [Description("catalogs add/remove: http(s) URL, file: URI, or absolute local or UNC path of a gallery JSON")] string? location = null,
        [Description("deploy: name of the new VM")] string? vmName = null,
        [Description("deploy: true creates a differencing child of the image; false copies it")] bool linked = true,
        int memoryMb = 4096,
        int processors = 2,
        [Description("deploy: virtual switch name; default is the Default Switch when present")] string? switchName = null,
        [Description("deploy: absolute folder for the VM (default: the host's virtual machine path)")] string? vmPath = null,
        [Description("deploy: start the VM after configuring it")] bool start = false,
        CancellationToken ct = default) => Run(async () => action switch
    {
        "list" => await service.Images.ListAsync(ct),
        "fetch" => await service.Images.FetchAsync(imageName, catalog, keepArchive, waitSeconds, ct),
        "register" => await service.Images.RegisterAsync(path, name, osFamily, kind, secureBoot, notes, ct),
        "remove" => await service.Images.RemoveAsync(name ?? imageName, deleteFile, ct),
        "catalogs" => await service.Images.CatalogsAsync(catalogAction, catalog, location, ct),
        "deploy" => await service.Images.DeployAsync(new ImageDeployRequest(imageName ?? "", vmName ?? "", linked, memoryMb, processors, switchName, notes, start, vmPath),
            service.Lifecycle, ct),
        _ => throw new ArgumentException("Use list, fetch, register, remove, catalogs or deploy.")
    });
}
