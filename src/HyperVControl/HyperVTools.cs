using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyperVControl.Extracted;
using HyperVControl.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static HyperVControl.Services.ControlService;

namespace HyperVControl;

[McpServerToolType]
internal sealed partial class HyperVTools(ControlService service)
{
    internal static CallToolResult Result(object? value)
    {
        var json = JsonSerializer.SerializeToElement(value, Program.Json);
        return new() { IsError = IsFailure(json), Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }

    internal static bool IsFailure(JsonElement json) => json.ValueKind == JsonValueKind.Object &&
            ((json.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
             || (json.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
             || (json.TryGetProperty("exitCode", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number && exitCode.GetInt32() != 0));
    internal static async Task<CallToolResult> Run(Func<Task<object?>> action)
    {
        try { return Result(await action()); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result(new { ok = false, error = ex.Message, errorType = ex.GetType().Name }); }
    }

    [McpServerTool(Name = "hyperv_capabilities", ReadOnly = true)]
    [Description("Probe local Hyper-V rights, host availability, console and guest prerequisites, and installed debugger versions. No VM mutations.")]
    public Task<CallToolResult> Capabilities(CancellationToken ct) => Run(async () => new
    {
        elevated = ElevationBridge.IsAdministrator, host = await service.HyperV.GetInventoryAsync(ct),
        debuggers = await service.Debugger.DiscoverAsync(ct),
        basic = "Hyper-V WMI framebuffer and synthetic input; console window uses RDP ActiveX on port 2179.",
        enhanced = "Supported guest, Enhanced Session enabled; RDP ActiveX. Capture requires foreground unobscured window.",
        guest = "PowerShell Direct requires a running Windows guest and guest credentials. UI Automation also needs an unlocked interactive sign-in.",
        elevation = "Use --elevate to bridge stdio to a same-user UAC worker. Cancelling UAC exits cleanly. Alternate-admin credentials require starting the MCP client in that account."
    });

    [McpServerTool(Name = "hyperv_list", ReadOnly = true)]
    [Description("List all local Hyper-V VMs with exact IDs, names, states, memory and CPUs.")]
    public Task<CallToolResult> List(CancellationToken ct) => Run(async () => await service.HyperV.GetInventoryAsync(ct));

    [McpServerTool(Name = "hyperv_vm_info", ReadOnly = true)]
    [Description("Read detailed VM hardware, integration services, disks, network, COM ports and current checkpoint ID.")]
    public Task<CallToolResult> Info(string machine, CancellationToken ct) => Run(async () => await service.InfoAsync((await service.ResolveAsync(machine, ct)).Id, ct));

    [McpServerTool(Name = "hyperv_power", Destructive = true)]
    [Description("Change VM power: start, shutdown (graceful), restart (graceful stop/wait/start), turn_off (hard), reset (hard), pause, resume, save. No automatic hard-power fallback.")]
    public Task<CallToolResult> Power(string machine, string action, CancellationToken ct) => Run(async () =>
    {
        var vm = await service.ResolveAsync(machine, ct);
        if (action == "reset") return await service.PowerShell.JsonAsync(Vm(vm.Id) + "Restart-VM -VM $vm -Force -Confirm:$false\n" + Vm(vm.Id) + VmJson, ct);
        var op = action switch { "start" => HyperVPowerAction.Start, "shutdown" => HyperVPowerAction.Shutdown,
            "turn_off" => HyperVPowerAction.TurnOff, "restart" => HyperVPowerAction.Restart, "pause" => HyperVPowerAction.Suspend,
            "resume" => HyperVPowerAction.Resume, "save" => HyperVPowerAction.Save, _ => throw new ArgumentException("Unknown power action.") };
        return await service.HyperV.ChangePowerStateAsync(vm.Id, op, ct);
    });

    [McpServerTool(Name = "hyperv_checkpoints", ReadOnly = true)]
    [Description("List a VM's checkpoint tree with IDs and its current parent checkpoint. Current is not necessarily newest.")]
    public Task<CallToolResult> Checkpoints(string machine, CancellationToken ct) => Run(async () => await service.HyperV.GetCheckpointsAsync((await service.ResolveAsync(machine, ct)).Id, ct));

    [McpServerTool(Name = "hyperv_checkpoint", Destructive = true)]
    [Description("Create, restore, remove, or rename one VM checkpoint. Resolve by exact name/ID; 'current' means current parent. Restore discards changes. Deletion does not implicitly include children.")]
    public Task<CallToolResult> Checkpoint(string machine, string action, string checkpoint, string? newName = null, CancellationToken ct = default) => Run(async () =>
    {
        var id = (await service.ResolveAsync(machine, ct)).Id;
        if (action == "create") return await service.HyperV.CreateCheckpointAsync(id, checkpoint, ct);
        var cid = await service.CheckpointIdAsync(id, checkpoint, ct);
        return action switch
        {
            "restore" => await service.HyperV.RestoreCheckpointAsync(id, cid, ct),
            "remove" => await service.HyperV.DeleteCheckpointAsync(id, cid, ct),
            "rename" when !string.IsNullOrWhiteSpace(newName) => await service.PowerShell.JsonAsync(Vm(id) + $"Get-VMSnapshot -VM $vm | Where-Object Id -EQ ([guid]'{cid:D}') | Rename-VMSnapshot -NewName {L(newName)}\n@{{success=$true}} | ConvertTo-Json -Compress", ct),
            _ => throw new ArgumentException("Use create/restore/remove/rename; rename needs newName.")
        };
    });

    [McpServerTool(Name = "hyperv_firmware", Destructive = true)]
    [Description("Set and verify Secure Boot on an Off Generation 2 VM. Change only for the requested guest task; debugging tools never disable it automatically.")]
    public Task<CallToolResult> Firmware(string machine, bool secureBoot, CancellationToken ct) => Run(async () => await service.HyperV.SetSecureBootAsync((await service.ResolveAsync(machine, ct)).Id, secureBoot, ct));

    [McpServerTool(Name = "hyperv_vm_create")]
    [Description("Create a Generation 1/2 VM with existing VHD or new VHDX. All paths are absolute host paths. Returns VM configuration; leaves VM Off.")]
    public Task<CallToolResult> CreateVm(string name, string path, string vhdPath, int generation = 2, int memoryMb = 4096,
        int processors = 2, int newDiskGb = 0, string? switchName = null, CancellationToken ct = default) => Run(async () =>
    {
        FullPath(path); FullPath(vhdPath);
        if (string.IsNullOrWhiteSpace(name) || generation is < 1 or > 2 || memoryMb is < 32 or > 1048576 || processors is < 1 or > 2048 || newDiskGb < 0)
            throw new ArgumentException("Invalid VM name, generation, memory, processors or disk size.");
        var disk = newDiskGb == 0 ? $"-VHDPath {L(vhdPath)}" : $"-NewVHDPath {L(vhdPath)} -NewVHDSizeBytes ([long]{newDiskGb}*1GB)";
        var net = switchName == null ? "" : "-SwitchName " + L(switchName);
        return await service.PowerShell.JsonAsync($"$vm=New-VM -Name {L(name)} -Path {L(path)} -Generation {generation} -MemoryStartupBytes ([long]{memoryMb}*1MB) {disk} {net}\nSet-VMProcessor -VM $vm -Count {processors}\n" + VmJson, ct);
    });

    [McpServerTool(Name = "hyperv_vm_remove", Destructive = true)]
    [Description("Unregister exactly one Off VM by GUID. Hyper-V checkpoint merges may occur. Leaves standalone VHD files on disk.")]
    public Task<CallToolResult> RemoveVm(string machineId, CancellationToken ct) => Run(async () =>
    {
        var id = Guid.Parse(machineId);
        return await service.PowerShell.JsonAsync(Vm(id) + RequireOff + "Remove-VM -VM $vm -Force\n@{success=$true} | ConvertTo-Json -Compress", ct, 300);
    });

    [McpServerTool(Name = "hyperv_vm_configure", Destructive = true)]
    [Description("Configure an Off VM's CPU, memory, dynamic memory, checkpoint type or name. Omitted values remain unchanged.")]
    public Task<CallToolResult> ConfigureVm(string machine, int? processors = null, int? memoryMb = null,
        bool? dynamicMemory = null, string? checkpointType = null, string? newName = null, CancellationToken ct = default) => Run(async () =>
    {
        var id = (await service.ResolveAsync(machine, ct)).Id;
        var script = Vm(id) + RequireOff;
        if (processors is not null) { if (processors is < 1 or > 2048) throw new ArgumentException("Invalid processors."); script += $"Set-VMProcessor -VM $vm -Count {processors}\n"; }
        if (memoryMb is not null) { if (memoryMb is < 32 or > 1048576) throw new ArgumentException("Invalid memoryMb."); script += $"Set-VMMemory -VM $vm -StartupBytes ([long]{memoryMb}*1MB)\n"; }
        if (dynamicMemory is not null) script += $"Set-VMMemory -VM $vm -DynamicMemoryEnabled:${dynamicMemory.ToString()!.ToLowerInvariant()}\n";
        if (checkpointType is not null)
        {
            if (checkpointType is not ("Standard" or "Production" or "ProductionOnly" or "Disabled")) throw new ArgumentException("Invalid checkpointType.");
            script += $"Set-VM -VM $vm -CheckpointType {checkpointType}\n";
        }
        if (newName is not null) script += "Rename-VM -VM $vm -NewName " + L(newName) + "\n";
        return await service.PowerShell.JsonAsync(script + Vm(id) + VmJson, ct);
    });

    [McpServerTool(Name = "hyperv_vm_export")]
    [Description("Export a VM including its checkpoint chain to an absolute host directory.")]
    public Task<CallToolResult> Export(string machine, string destination, CancellationToken ct) => Run(async () =>
        await service.PowerShell.JsonAsync(Vm((await service.ResolveAsync(machine, ct)).Id) + $"Export-VM -VM $vm -Path {L(FullPath(destination))}\n@{{success=$true}} | ConvertTo-Json -Compress", ct, 1800));

    [McpServerTool(Name = "hyperv_vm_import")]
    [Description("Import an exported .vmcx by copying it and generating a new VM ID. This is also the clone path. Supply fresh VM and VHD destination directories.")]
    public Task<CallToolResult> Import(string configurationPath, string vmDestination, string vhdDestination, CancellationToken ct) => Run(async () =>
        await service.PowerShell.JsonAsync($"$vm=Import-VM -Path {L(FullPath(configurationPath))} -Copy -GenerateNewId -VirtualMachinePath {L(FullPath(vmDestination))} -VhdDestinationPath {L(FullPath(vhdDestination))}\n" + VmJson, ct, 1800));

    [McpServerTool(Name = "hyperv_network", Destructive = true)]
    [Description("List virtual switches, create private/internal switches, remove an exact switch, or connect/disconnect a named VM adapter. External switch creation is deliberately explicit via host administration.")]
    public Task<CallToolResult> Network(string action = "list", string? machine = null, string? switchName = null, string adapterName = "Network Adapter", string switchType = "Private", CancellationToken ct = default) => Run(async () =>
    {
        if (action == "list") return await service.PowerShell.JsonAsync("ConvertTo-Json -InputObject @(Get-VMSwitch | Select-Object Id,Name,SwitchType,NetAdapterInterfaceDescription) -Compress", ct);
        if (action == "create")
        {
            if (switchType is not ("Private" or "Internal") || string.IsNullOrWhiteSpace(switchName)) throw new ArgumentException("Name and Private/Internal switchType required.");
            return await service.PowerShell.JsonAsync($"New-VMSwitch -Name {L(switchName)} -SwitchType {switchType} | Select-Object Id,Name,SwitchType | ConvertTo-Json -Compress", ct);
        }
        if (action == "remove")
            return await service.PowerShell.JsonAsync($"$s=@(Get-VMSwitch | Where-Object Name -EQ {L(switchName ?? throw new ArgumentException("switchName required."))}); if($s.Count -ne 1){{throw 'Switch missing or ambiguous'}}; $s[0] | Remove-VMSwitch -Force\n@{{success=$true}} | ConvertTo-Json -Compress", ct);
        var id = (await service.ResolveAsync(machine ?? throw new ArgumentException("machine required."), ct)).Id;
        var select = Vm(id) + $"$a=@(Get-VMNetworkAdapter -VM $vm | Where-Object Name -EQ {L(adapterName)}); if($a.Count -ne 1){{throw 'Adapter missing or ambiguous'}}\n";
        var op = action switch { "connect" => $"Connect-VMNetworkAdapter -VMNetworkAdapter $a[0] -SwitchName {L(switchName ?? throw new ArgumentException("switchName required."))}",
            "disconnect" => "Disconnect-VMNetworkAdapter -VMNetworkAdapter $a[0]", _ => throw new ArgumentException("Unknown network action.") };
        return await service.PowerShell.JsonAsync(select + op + "\n@{success=$true} | ConvertTo-Json -Compress", ct);
    });

    [McpServerTool(Name = "hyperv_disk", Destructive = true)]
    [Description("Manage VM media: list, attach_vhd, detach_vhd by exact path, attach_iso, eject_iso, or resize_vhd. Changes require VM Off; does not delete disk files.")]
    public Task<CallToolResult> Disk(string machine, string action = "list", string? path = null, long? sizeGb = null, CancellationToken ct = default) => Run(async () =>
    {
        var id = (await service.ResolveAsync(machine, ct)).Id;
        if (action == "list") return await service.InfoAsync(id, ct);
        var script = Vm(id) + RequireOff;
        var literal = path == null ? "" : L(FullPath(path));
        if (action is not "eject_iso" && path == null) throw new ArgumentException("path required.");
        script += action switch
        {
            "attach_vhd" => $"Add-VMHardDiskDrive -VM $vm -Path {literal}",
            "detach_vhd" => $"Get-VMHardDiskDrive -VM $vm | Where-Object Path -EQ {literal} | Remove-VMHardDiskDrive",
            "attach_iso" => $"$d=@(Get-VMDvdDrive -VM $vm); if($d.Count -eq 0){{Add-VMDvdDrive -VM $vm -Path {literal}}} elseif($d.Count -eq 1){{$d[0] | Set-VMDvdDrive -Path {literal}}} else{{throw 'Multiple DVD drives; use an exact device configuration'}}",
            "eject_iso" => "Get-VMDvdDrive -VM $vm | Set-VMDvdDrive -Path $null",
            "resize_vhd" when sizeGb > 0 => $"$d=@(Get-VMHardDiskDrive -VM $vm | Where-Object Path -EQ {literal}); if($d.Count -ne 1){{throw 'Disk is not attached exactly once to this VM'}}; Resize-VHD -Path {literal} -SizeBytes ([long]{sizeGb}*1GB)",
            _ => throw new ArgumentException("Unknown disk action or missing sizeGb.")
        };
        return await service.PowerShell.JsonAsync(script + "\n@{success=$true} | ConvertTo-Json -Compress", ct, 300);
    });
}
