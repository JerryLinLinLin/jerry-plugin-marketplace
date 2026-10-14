using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HyperVControl;

/// <summary>VM lifecycle, media and network tools: thin wrappers over <c>VmLifecycleService</c>.</summary>
internal sealed partial class HyperVTools
{
    [McpServerTool(Name = "hyperv_vm_create")]
    [Description("Create a Generation 1/2 VM with existing VHD or new VHDX. All paths are absolute host paths. Returns VM configuration; leaves VM Off.")]
    public Task<CallToolResult> CreateVm(string name, string path, string vhdPath, int generation = 2, int memoryMb = 4096,
        int processors = 2, int newDiskGb = 0, string? switchName = null, CancellationToken ct = default) => Run(async () =>
        await service.Lifecycle.CreateAsync(name, path, vhdPath, generation, memoryMb, processors, newDiskGb, switchName, ct));

    [McpServerTool(Name = "hyperv_vm_remove", Destructive = true)]
    [Description("Unregister exactly one Off VM by GUID. Hyper-V checkpoint merges may occur. Leaves standalone VHD files on disk.")]
    public Task<CallToolResult> RemoveVm(string machineId, CancellationToken ct) => Run(async () =>
        await service.Lifecycle.RemoveAsync(Guid.Parse(machineId), ct));

    [McpServerTool(Name = "hyperv_vm_configure", Destructive = true)]
    [Description("Configure an Off VM's CPU, memory, dynamic memory, checkpoint type or name. Omitted values remain unchanged.")]
    public Task<CallToolResult> ConfigureVm(string machine, int? processors = null, int? memoryMb = null,
        bool? dynamicMemory = null, string? checkpointType = null, string? newName = null, CancellationToken ct = default) => Run(async () =>
        await service.Lifecycle.ConfigureAsync(machine, processors, memoryMb, dynamicMemory, checkpointType, newName, ct));

    [McpServerTool(Name = "hyperv_vm_export")]
    [Description("Export a VM including its checkpoint chain to an absolute host directory.")]
    public Task<CallToolResult> Export(string machine, string destination, CancellationToken ct) => Run(async () =>
        await service.Lifecycle.ExportAsync(machine, destination, ct));

    [McpServerTool(Name = "hyperv_vm_import")]
    [Description("Import an exported .vmcx by copying it and generating a new VM ID. This is also the clone path. Supply fresh VM and VHD destination directories.")]
    public Task<CallToolResult> Import(string configurationPath, string vmDestination, string vhdDestination, CancellationToken ct) => Run(async () =>
        await service.Lifecycle.ImportAsync(configurationPath, vmDestination, vhdDestination, ct));

    [McpServerTool(Name = "hyperv_network", Destructive = true)]
    [Description("List virtual switches, create private/internal switches, remove an exact switch, or connect/disconnect a named VM adapter. External switch creation is deliberately explicit via host administration.")]
    public Task<CallToolResult> Network(string action = "list", string? machine = null, string? switchName = null, string adapterName = "Network Adapter", string switchType = "Private", CancellationToken ct = default) => Run(async () => action switch
    {
        "list" => await service.Lifecycle.ListSwitchesAsync(ct),
        "create" => await service.Lifecycle.CreateSwitchAsync(switchName ?? throw new ArgumentException("Name and Private/Internal switchType required."), switchType, ct),
        "remove" => await service.Lifecycle.RemoveSwitchAsync(switchName ?? throw new ArgumentException("switchName required."), ct),
        "connect" => await service.Lifecycle.ConnectAdapterAsync(machine ?? throw new ArgumentException("machine required."), adapterName, switchName ?? throw new ArgumentException("switchName required."), ct),
        "disconnect" => await service.Lifecycle.ConnectAdapterAsync(machine ?? throw new ArgumentException("machine required."), adapterName, null, ct),
        _ => throw new ArgumentException("Unknown network action.")
    });

    [McpServerTool(Name = "hyperv_disk", Destructive = true)]
    [Description("Manage VM media: list, attach_vhd, detach_vhd by exact path, attach_iso, eject_iso, or resize_vhd. Changes require VM Off; does not delete disk files.")]
    public Task<CallToolResult> Disk(string machine, string action = "list", string? path = null, long? sizeGb = null, CancellationToken ct = default) => Run(async () =>
        action == "list" ? await service.Lifecycle.InfoAsync(machine, ct) : await service.Lifecycle.DiskAsync(machine, action, path, sizeGb, ct));
}
