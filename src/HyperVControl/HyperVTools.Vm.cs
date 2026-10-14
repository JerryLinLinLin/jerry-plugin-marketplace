using System.ComponentModel;
using HyperVControl.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HyperVControl;

/// <summary>VM lifecycle, storage, media and network tools: thin wrappers over <c>VmLifecycleService</c>.</summary>
internal sealed partial class HyperVTools
{
    /// <summary>A job that failed or was cancelled within the wait is reported as a tool error, with the job attached.</summary>
    private static object JobOutcome(JobInfo job) => job.Status is JobStatus.Failed or JobStatus.Cancelled
        ? new { ok = false, error = job.Error ?? "The job failed.", errorType = job.ErrorType ?? "JobFailed", job }
        : job;

    [McpServerTool(Name = "hyperv_vm_create")]
    [Description("Create a VM; it stays Off unless start=true. Default source=image: a linked clone (seconds) of imageName or of the default image, Microsoft's Quick Create 'Windows 11 dev environment' (a Windows 11 Enterprise evaluation) unless HYPERV_CONTROL_DEFAULT_IMAGE names another; hyperv_image list shows it as defaultImage. An image not yet in the library is fetched by one hyperv_jobs job that then creates the VM (the job's result is the VM; waitSeconds bounds the wait): the Windows image's first use downloads 23.3 GB and extracts a 44.8 GB VHDX, needing about 69 GB free in the library (HYPERV_CONTROL_IMAGE_ROOT moves it), so confirm with the user first. The name, switch and target disk are checked before any download, and a running fetch of the same image is joined, never repeated. Image VMs take the image's OS family and Secure Boot setting; parameters only other sources use are refused. Other sources: new_disk (a 64 GB dynamic VHDX by default), iso, existing_vhd, linked, none. Defaults: Generation 2, 4096 MB static memory, 2 vCPUs, Standard checkpoints with automatic checkpoints off, the Default Switch (else the first switch). The VM folder is <path>\\<name> (path defaults to the host VirtualMachinePath); new disks go to <path>\\<name>\\Virtual Hard Disks when path is given (always for image clones), else the host VirtualHardDiskPath. osFamily windows: Secure Boot with the MicrosoftWindows template and a vTPM with a local key protector; linux: Secure Boot with MicrosoftUEFICertificateAuthority and the HvSocket enhanced-session transport; other: Secure Boot off. Refuses an existing VM name (unless allowDuplicateName), an existing target disk file, a disk another VM uses, and a disk larger than the volume's free space, all before any change; a failed later step removes the half-created VM and its new disks. Returns the VM with firmware, security, createdFiles, warnings and nextSteps; source=image returns {vm, disk, parent, started, warnings}, or the job (plus next while it runs).")]
    public Task<CallToolResult> CreateVm(string name,
        [Description("image (default: linked clone of imageName or the default image, fetched first as a job when not in the library; same as hyperv_image deploy) | new_disk (empty dynamic VHDX of diskGb) | iso (new disk plus a DVD drive with isoPath, set as first boot device) | existing_vhd (attach vhdPath) | linked (differencing disk over parentPath, which is marked read-only) | none (no disk)")] string source = "image",
        [Description("image: a library entry, else a catalog image, by exact name from hyperv_image list. Omit for the default image (hyperv_image list shows it as defaultImage).")] string? imageName = null,
        [Description("image: the catalog to search when the image name is in several catalogs.")] string? catalog = null,
        [Description("image: seconds to wait for a fetch-then-create job before returning it (0-3600, default 60); then poll hyperv_jobs action=wait. A library image is deployed within the call.")] int waitSeconds = 60,
        [Description("existing_vhd: the disk to attach. new_disk/iso: optional target .vhdx path. linked: optional path of the differencing child.")] string? vhdPath = null,
        [Description("linked: the parent .vhdx; no VM may attach it.")] string? parentPath = null,
        [Description("iso: installation ISO.")] string? isoPath = null,
        [Description("Absolute folder for the VM's own folder; default the host VirtualMachinePath.")] string? path = null,
        int generation = 2, int memoryMb = 4096, bool dynamicMemory = false, int? memoryMinMb = null, int? memoryMaxMb = null,
        int processors = 2, int diskGb = 64,
        [Description("Exact switch name. Omit for the Default Switch (or the first switch); an empty string leaves the adapter disconnected.")] string? switchName = null,
        [Description("windows | linux | other; sets the Secure Boot, vTPM and enhanced-session defaults. source=image uses the image's OS family instead.")] string osFamily = "windows",
        [Description("Default: on for windows and linux, off for other.")] bool? secureBoot = null,
        [Description("Default: on for Generation 2 windows (local key protector).")] bool? tpm = null,
        string? notes = null, bool autoCheckpoints = false,
        [Description("Standard | Production | ProductionOnly | Disabled")] string checkpointType = "Standard",
        bool start = false, bool allowDuplicateName = false, CancellationToken ct = default) => Run(async () =>
    {
        if (waitSeconds is < 0 or > 3600) throw new ArgumentException("waitSeconds must be between 0 and 3600.");
        if (source == "image")
        {
            // The default source must never turn a call meant for another source into a multi-GB download.
            var unused = new (bool Given, string Name)[]
            {
                (vhdPath is not null, "vhdPath"), (parentPath is not null, "parentPath"), (isoPath is not null, "isoPath"), (generation != 2, "generation"),
                (diskGb != 64, "diskGb"), (dynamicMemory, "dynamicMemory"), (memoryMinMb is not null, "memoryMinMb"), (memoryMaxMb is not null, "memoryMaxMb"),
                (secureBoot is not null, "secureBoot"), (tpm is not null, "tpm"), (autoCheckpoints, "autoCheckpoints"), (checkpointType != "Standard", "checkpointType")
            }.Where(p => p.Given).Select(p => p.Name).ToList();
            if (unused.Count > 0)
                throw new ArgumentException($"source=image (the default) does not use {string.Join(", ", unused)}: an image VM is a Generation 2 linked clone with the image's OS family and Secure Boot setting "
                    + "and the lab defaults (static memory, Standard checkpoints, automatic checkpoints off). Pass source=new_disk, iso, existing_vhd, linked or none to use them, "
                    + "or change the clone afterwards with hyperv_vm_configure and hyperv_firmware.");
            return await service.Images.CreateVmAsync(imageName, catalog,
                new ImageDeployRequest("", name, true, memoryMb, processors, switchName, notes, start, path, allowDuplicateName), service.Lifecycle, waitSeconds, ct);
        }
        if (source is not ("new_disk" or "iso" or "existing_vhd" or "linked" or "none"))
            throw new ArgumentException("source must be one of: image (the default), new_disk, iso, existing_vhd, linked, none.");
        if (imageName is not null) throw new ArgumentException("imageName applies only to source=image.");
        if (catalog is not null) throw new ArgumentException("catalog applies only to source=image.");
        return await service.Lifecycle.CreateAsync(new VmCreateRequest(name)
        {
            Source = source, VhdPath = vhdPath, ParentPath = parentPath, IsoPath = isoPath, Path = path, Generation = generation,
            MemoryMb = memoryMb, DynamicMemory = dynamicMemory, MemoryMinMb = memoryMinMb, MemoryMaxMb = memoryMaxMb, Processors = processors,
            DiskGb = diskGb, SwitchName = switchName, OsFamily = osFamily, SecureBoot = secureBoot, Tpm = tpm, Notes = notes,
            AutoCheckpoints = autoCheckpoints, CheckpointType = checkpointType, Start = start, AllowDuplicateName = allowDuplicateName
        }, ct);
    });

    [McpServerTool(Name = "hyperv_vm_remove", Destructive = true)]
    [Description("Remove one VM by exact name or ID. It must be Off or Saved: this tool never powers a VM off. Without deleteDisks only the VM's configuration and runtime state are deleted; every disk file stays, and Hyper-V merges checkpoint .avhdx files into their parents in the background. With deleteDisks the VM's checkpoints are removed first and their merges awaited, then the VM, then its VHD/VHDX files, except files another VM attaches, differencing parents of other disks (linked-clone templates) and ISOs; each deletion is verified, and an emptied VM folder is removed. dryRun=true returns that plan (checkpoints, merge, delete, keep with reasons) and changes nothing. Returns what was removed, deleted and kept, with reasons.")]
    public Task<CallToolResult> RemoveVm(string machine, bool deleteDisks = false, bool dryRun = false, CancellationToken ct = default) => Run(async () =>
        await service.Lifecycle.RemoveAsync(machine, deleteDisks, dryRun, ct));

    [McpServerTool(Name = "hyperv_vm_configure", Destructive = true)]
    [Description("Change VM settings; omitted values stay unchanged. The VM's state decides what is allowed, all-or-nothing: a refusal names every setting that needs the VM Off and changes nothing. Running accepts memoryMb with static memory (the guest OS must support resize), memoryMaxMb increases, memoryMinMb decreases and memoryBufferPercent with dynamic memory, checkpointType, autoCheckpoints, newName, notes, autoStartAction, autoStartDelaySeconds and integrationServices. Saved accepts newName only. Off is needed for processors, dynamicMemory, autoStopAction, nestedVirtualization and enhancedSessionTransport. Paused and transitional states accept nothing. Returns the refreshed VM.")]
    public Task<CallToolResult> ConfigureVm(string machine, int? processors = null, int? memoryMb = null, bool? dynamicMemory = null,
        [Description("Standard | Production | ProductionOnly | Disabled")] string? checkpointType = null, string? newName = null,
        int? memoryMinMb = null, int? memoryMaxMb = null, int? memoryBufferPercent = null, string? notes = null,
        [Description("Nothing | StartIfRunning | Start")] string? autoStartAction = null, int? autoStartDelaySeconds = null,
        [Description("TurnOff | Save | ShutDown")] string? autoStopAction = null, bool? autoCheckpoints = null, bool? nestedVirtualization = null,
        [Description("Service name to enabled, e.g. {\"Guest Service Interface\": true}. Names: Guest Service Interface, Heartbeat, Key-Value Pair Exchange, Shutdown, Time Synchronization, VSS.")] Dictionary<string, bool>? integrationServices = null,
        [Description("VMBus | HvSocket (Linux xrdp)")] string? enhancedSessionTransport = null, CancellationToken ct = default) => Run(async () =>
        await service.Lifecycle.ConfigureAsync(machine, new VmConfigureRequest
        {
            Processors = processors, MemoryMb = memoryMb, DynamicMemory = dynamicMemory, CheckpointType = checkpointType, NewName = newName,
            MemoryMinMb = memoryMinMb, MemoryMaxMb = memoryMaxMb, MemoryBufferPercent = memoryBufferPercent, Notes = notes,
            AutoStartAction = autoStartAction, AutoStartDelaySeconds = autoStartDelaySeconds, AutoStopAction = autoStopAction,
            AutoCheckpoints = autoCheckpoints, NestedVirtualization = nestedVirtualization, IntegrationServices = integrationServices,
            EnhancedSessionTransport = enhancedSessionTransport
        }, ct));

    [McpServerTool(Name = "hyperv_vm_clone", Destructive = true)]
    [Description("Clone an Off VM as newName; the clone stays Off. mode=linked: a differencing disk over each of the source's current disks plus a new VM with the source's generation, memory, vCPUs, Secure Boot, checkpoint settings and adapters (new MACs) and, when the source has one, a new vTPM. The source disks become read-only parents: the source can no longer start, and changing them would break every linked clone. Sources with checkpoints are refused. mode=full: Export-VM, then Import-VM -Copy -GenerateNewId into <destination>\\<newName> (destination defaults to the host VirtualMachinePath) with new MACs and disk identifiers; synchronous and slow for large disks. Both copy the guest installation (same computer name and SID); sysprep the source for independent machines.")]
    public Task<CallToolResult> CloneVm(string machine, string newName, [Description("linked | full")] string mode,
        [Description("Absolute folder that receives <newName>; default the host VirtualMachinePath.")] string? destination = null,
        [Description("full: seconds to wait for the clone job before returning its state (0-3600, default 60); poll hyperv_jobs afterwards. linked clones complete in-call.")] int waitSeconds = 60,
        CancellationToken ct = default) => Run(async () =>
    {
        if (mode != "full") return await service.Lifecycle.CloneAsync(machine, newName, mode, destination, ct);
        if (waitSeconds is < 0 or > 3600) throw new ArgumentException("waitSeconds must be 0..3600.");
        return JobOutcome(await service.Jobs.StartAndWaitAsync($"clone {machine} to {newName}",
            async (progress, token) => (object?)await service.Lifecycle.CloneAsync(machine, newName, mode, destination, token), waitSeconds, ct));
    });

    [McpServerTool(Name = "hyperv_vhd", Destructive = true)]
    [Description("Standalone virtual disk files at absolute paths. create: a dynamic or fixed .vhdx/.vhd of sizeGb, or a differencing disk over parentPath (the parent is then marked read-only; refused when a VM attaches it); never overwrites. inspect: format, type, sizes, parent chain, read-only flag, the VMs that attach it and the disks it is a parent of. delete: refuses a disk a VM attaches (also through checkpoints), a differencing parent of another disk, or one mounted on the host; dryRun=true reports whether it could be deleted and why not, changing nothing.")]
    public Task<CallToolResult> Vhd([Description("create | inspect | delete")] string action, string path,
        [Description("create: dynamic | fixed | differencing")] string type = "dynamic", long? sizeGb = null, string? parentPath = null,
        bool dryRun = false, CancellationToken ct = default) => Run(async () =>
        await service.Lifecycle.VhdAsync(action, path, type, sizeGb, parentPath, dryRun, ct));

    [McpServerTool(Name = "hyperv_vm_export")]
    [Description("Export a VM including its checkpoint chain to an absolute host directory, as a hyperv_jobs job. Returns the job state after waitSeconds (0-3600, default 60); poll hyperv_jobs for the rest.")]
    public Task<CallToolResult> Export(string machine, string destination, int waitSeconds = 60, CancellationToken ct = default) => Run(async () =>
    {
        if (waitSeconds is < 0 or > 3600) throw new ArgumentException("waitSeconds must be 0..3600.");
        return JobOutcome(await service.Jobs.StartAndWaitAsync($"export {machine}",
            async (progress, token) => (object?)await service.Lifecycle.ExportAsync(machine, destination, token), waitSeconds, ct));
    });

    [McpServerTool(Name = "hyperv_vm_import")]
    [Description("Import an exported .vmcx by copying it and generating a new VM ID, as a hyperv_jobs job. Supply fresh VM and VHD destination directories. Returns the job state after waitSeconds (0-3600, default 60); poll hyperv_jobs for the rest. hyperv_vm_clone mode=full does export plus import in one call.")]
    public Task<CallToolResult> Import(string configurationPath, string vmDestination, string vhdDestination, int waitSeconds = 60, CancellationToken ct = default) => Run(async () =>
    {
        if (waitSeconds is < 0 or > 3600) throw new ArgumentException("waitSeconds must be 0..3600.");
        return JobOutcome(await service.Jobs.StartAndWaitAsync($"import {configurationPath}",
            async (progress, token) => (object?)await service.Lifecycle.ImportAsync(configurationPath, vmDestination, vhdDestination, token), waitSeconds, ct));
    });

    [McpServerTool(Name = "hyperv_network", Destructive = true)]
    [Description("Virtual switches and VM network adapters. list; create (switchType Private | Internal | NAT: an Internal switch whose host adapter takes the first address of natSubnet, e.g. 192.168.200.0/24, plus a WinNAT network; WinNAT has no DHCP); remove (exact switchName, plus a NAT network of the same name); connect / disconnect (adapterName to switchName); add_adapter (adapterName unique on the VM, optional switchName); remove_adapter; set_adapter (staticMac such as 00155D0A0B0C or 'dynamic', macSpoofing, vlanId 1-4094 or 0 for untagged). Running VMs accept connect/disconnect, macSpoofing, vlanId, and Generation 2 adapter add/remove; staticMac needs Off. External switches are not created here.")]
    public Task<CallToolResult> Network([Description("list | create | remove | connect | disconnect | add_adapter | remove_adapter | set_adapter")] string action = "list",
        string? machine = null, string? switchName = null, string adapterName = "Network Adapter",
        [Description("create: Private | Internal | NAT")] string switchType = "Private", string? natSubnet = null,
        string? staticMac = null, bool? macSpoofing = null, int? vlanId = null, CancellationToken ct = default) => Run(async () =>
    {
        string Machine() => machine ?? throw new ArgumentException("machine required.");
        return action switch
        {
            "list" => await service.Lifecycle.ListSwitchesAsync(ct),
            "create" => await service.Lifecycle.CreateSwitchAsync(switchName ?? throw new ArgumentException("switchName required."), switchType, natSubnet, ct),
            "remove" => await service.Lifecycle.RemoveSwitchAsync(switchName ?? throw new ArgumentException("switchName required."), ct),
            "connect" => await service.Lifecycle.ConnectAdapterAsync(Machine(), adapterName, switchName ?? throw new ArgumentException("switchName required."), ct),
            "disconnect" => await service.Lifecycle.ConnectAdapterAsync(Machine(), adapterName, null, ct),
            "add_adapter" => await service.Lifecycle.AddAdapterAsync(Machine(), adapterName, switchName, ct),
            "remove_adapter" => await service.Lifecycle.RemoveAdapterAsync(Machine(), adapterName, ct),
            "set_adapter" => await service.Lifecycle.SetAdapterAsync(Machine(), adapterName, staticMac, macSpoofing, vlanId, ct),
            _ => throw new ArgumentException("Unknown network action.")
        };
    });

    [McpServerTool(Name = "hyperv_disk", Destructive = true)]
    [Description("VM media: list, attach_vhd (refused when another VM attaches the file or it is a differencing parent), detach_vhd by exact path, attach_iso (into the DVD drive, adding one when missing), eject_iso, resize_vhd (sizeGb; refused for checkpoint files and shared parents). Running VMs accept SCSI disk attach/detach (never the boot disk), ISO insert/eject, a Generation 2 DVD drive add, and online resize of a VHDX on SCSI; Saved and Paused VMs accept no change. Never deletes disk files (hyperv_vhd does).")]
    public Task<CallToolResult> Disk(string machine, [Description("list | attach_vhd | detach_vhd | attach_iso | eject_iso | resize_vhd")] string action = "list",
        string? path = null, long? sizeGb = null, CancellationToken ct = default) => Run(async () =>
        action == "list" ? await service.Lifecycle.InfoAsync(machine, ct) : await service.Lifecycle.DiskAsync(machine, action, path, sizeGb, ct));
}
