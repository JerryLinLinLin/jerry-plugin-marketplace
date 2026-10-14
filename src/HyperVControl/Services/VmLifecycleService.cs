using System.Text.Json;

namespace HyperVControl.Services;

/// <summary>
/// VM lifecycle and hardware operations on the local host. Every operation builds one
/// <see cref="HyperVScript"/> and runs it through the shared runner, so a tool call costs one
/// PowerShell process, every failure carries the uniform envelope, and tests can drive this
/// layer against a mock Hyper-V module without a Hyper-V host.
/// </summary>
internal sealed class VmLifecycleService(PowerShellRunner runner)
{
    internal const string VmProperties = "Id,Name,State,Generation,ProcessorCount,MemoryStartup,MemoryAssigned,DynamicMemoryEnabled,CheckpointType,Path";
    internal const string VmProjection = "($vm | Microsoft.PowerShell.Utility\\Select-Object " + VmProperties + ")";
    private const string Success = "@{ success = $true }";
    private const string Refresh = "$vm = Hyper-V\\Get-VM -Id $vm.Id -ErrorAction Stop";
    private static readonly string[] CheckpointTypes = ["Standard", "Production", "ProductionOnly", "Disabled"];

    private static PsValue VmRef => PsValue.Raw("$vm");

    public Task<JsonElement> InfoAsync(string machine, CancellationToken ct) => runner.InvokeAsync(new HyperVScript().Vm(machine).Returns("""
        @{ vm=($vm | Select-Object Id,Name,State,Generation,Version,ProcessorCount,MemoryStartup,MemoryAssigned,MemoryMinimum,MemoryMaximum,DynamicMemoryEnabled,CheckpointType,ParentSnapshotId,Path);
           firmware=$(if($vm.Generation -eq 2){ Get-VMFirmware -VM $vm | Select-Object @{Name='SecureBoot';Expression={$_.SecureBoot.ToString()}},SecureBootTemplate,PreferredNetworkBootProtocol }else{$null});
           network=@(Get-VMNetworkAdapter -VM $vm | Select-Object Id,Name,SwitchName,MacAddress,IPAddresses,Status);
           disks=@(Get-VMHardDiskDrive -VM $vm | Select-Object Path,ControllerType,ControllerNumber,ControllerLocation);
           dvd=@(Get-VMDvdDrive -VM $vm | Select-Object Path,ControllerNumber,ControllerLocation);
           com=@(Get-VMComPort -VM $vm | Select-Object Name,Path);
           integration=@(Get-VMIntegrationService -VM $vm | Select-Object Id,Name,Enabled,PrimaryStatusDescription)
        }
        """), ct);

    public Task<JsonElement> CreateAsync(string name, string path, string vhdPath, int generation, int memoryMb, int processors, int newDiskGb, string? switchName, CancellationToken ct)
    {
        ControlService.FullPath(path); ControlService.FullPath(vhdPath);
        if (string.IsNullOrWhiteSpace(name) || generation is < 1 or > 2 || memoryMb is < 32 or > 1048576 || processors is < 1 or > 2048 || newDiskGb < 0)
            throw new ArgumentException("Invalid VM name, generation, memory, processors or disk size.");
        var script = new HyperVScript()
            .Cmd("$vm = New-VM",
                ("Name", PsValue.Of(name)), ("Path", PsValue.Of(path)), ("Generation", PsValue.Of(generation)),
                ("MemoryStartupBytes", PsValue.Megabytes(memoryMb)),
                newDiskGb == 0 ? ("VHDPath", PsValue.Of(vhdPath)) : ("NewVHDPath", PsValue.Of(vhdPath)),
                ("NewVHDSizeBytes", newDiskGb == 0 ? null : PsValue.Gigabytes(newDiskGb)),
                ("SwitchName", switchName is null ? null : PsValue.Of(switchName)))
            .Cmd("Set-VMProcessor", ("VM", VmRef), ("Count", PsValue.Of(processors)))
            .Returns(VmProjection);
        return runner.InvokeAsync(script, ct);
    }

    /// <summary>Unregisters an Off VM. Disk files are never deleted here.</summary>
    public Task<JsonElement> RemoveAsync(Guid machineId, CancellationToken ct) => runner.InvokeAsync(
        new HyperVScript().Vm(machineId).RequireOff().Cmd("Remove-VM", ("VM", VmRef), ("Force", PsValue.Of(true))).Returns(Success), ct, 300);

    public Task<JsonElement> ConfigureAsync(string machine, int? processors, int? memoryMb, bool? dynamicMemory, string? checkpointType, string? newName, CancellationToken ct)
    {
        if (processors is < 1 or > 2048) throw new ArgumentException("Invalid processors.");
        if (memoryMb is < 32 or > 1048576) throw new ArgumentException("Invalid memoryMb.");
        var script = new HyperVScript().Vm(machine).RequireOff();
        if (processors is not null) script.Cmd("Set-VMProcessor", ("VM", VmRef), ("Count", PsValue.Of(processors.Value)));
        if (memoryMb is not null) script.Cmd("Set-VMMemory", ("VM", VmRef), ("StartupBytes", PsValue.Megabytes(memoryMb.Value)));
        if (dynamicMemory is not null) script.Cmd("Set-VMMemory", ("VM", VmRef), ("DynamicMemoryEnabled", PsValue.Of(dynamicMemory.Value)));
        if (checkpointType is not null) script.Cmd("Set-VM", ("VM", VmRef), ("CheckpointType", PsValue.Token(checkpointType, CheckpointTypes)));
        if (newName is not null) script.Cmd("Rename-VM", ("VM", VmRef), ("NewName", PsValue.Of(newName)));
        return runner.InvokeAsync(script.Line(Refresh).Returns(VmProjection), ct);
    }

    public Task<JsonElement> ExportAsync(string machine, string destination, CancellationToken ct) => runner.InvokeAsync(
        new HyperVScript().Vm(machine).Cmd("Export-VM", ("VM", VmRef), ("Path", PsValue.Of(ControlService.FullPath(destination)))).Returns(Success), ct, 1800);

    public Task<JsonElement> ImportAsync(string configurationPath, string vmDestination, string vhdDestination, CancellationToken ct) => runner.InvokeAsync(
        new HyperVScript().Cmd("$vm = Import-VM",
            ("Path", PsValue.Of(ControlService.FullPath(configurationPath))), ("Copy", PsValue.Of(true)), ("GenerateNewId", PsValue.Of(true)),
            ("VirtualMachinePath", PsValue.Of(ControlService.FullPath(vmDestination))), ("VhdDestinationPath", PsValue.Of(ControlService.FullPath(vhdDestination))))
        .Returns(VmProjection), ct, 1800);

    public Task<JsonElement> ListSwitchesAsync(CancellationToken ct) => runner.InvokeAsync(
        new HyperVScript().Returns("@(Get-VMSwitch | Select-Object Id,Name,SwitchType,NetAdapterInterfaceDescription)"), ct);

    public Task<JsonElement> CreateSwitchAsync(string name, string switchType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name and Private/Internal switchType required.");
        return runner.InvokeAsync(new HyperVScript()
            .Cmd("$switch = New-VMSwitch", ("Name", PsValue.Of(name)), ("SwitchType", PsValue.Token(switchType, "Private", "Internal")))
            .Returns("($switch | Select-Object Id,Name,SwitchType)"), ct);
    }

    public Task<JsonElement> RemoveSwitchAsync(string name, CancellationToken ct) => runner.InvokeAsync(new HyperVScript()
        .Line("$switches = @(Get-VMSwitch | Where-Object { $_.Name -eq " + PowerShellRunner.Literal(name) + " })")
        .Line("if ($switches.Count -ne 1) { Stop-Control 'NotFound' 'Switch missing or ambiguous' }")
        .Cmd("$switches[0] | Remove-VMSwitch", ("Force", PsValue.Of(true)))
        .Returns(Success), ct);

    public Task<JsonElement> ConnectAdapterAsync(string machine, string adapterName, string? switchName, CancellationToken ct)
    {
        var script = new HyperVScript().Vm(machine)
            .Line("$adapters = @(Get-VMNetworkAdapter -VM $vm | Where-Object { $_.Name -eq " + PowerShellRunner.Literal(adapterName) + " })")
            .Line("if ($adapters.Count -ne 1) { Stop-Control 'NotFound' 'Adapter missing or ambiguous' }");
        if (switchName is null) script.Cmd("Disconnect-VMNetworkAdapter", ("VMNetworkAdapter", PsValue.Raw("$adapters[0]")));
        else script.Cmd("Connect-VMNetworkAdapter", ("VMNetworkAdapter", PsValue.Raw("$adapters[0]")), ("SwitchName", PsValue.Of(switchName)));
        return runner.InvokeAsync(script.Returns(Success), ct);
    }

    /// <summary>Media changes: attach_vhd, detach_vhd, attach_iso, eject_iso, resize_vhd. Requires Off.</summary>
    public Task<JsonElement> DiskAsync(string machine, string action, string? path, long? sizeGb, CancellationToken ct)
    {
        if (action is not "eject_iso" && path == null) throw new ArgumentException("path required.");
        var literal = path == null ? "" : PowerShellRunner.Literal(ControlService.FullPath(path));
        var script = new HyperVScript().Vm(machine).RequireOff();
        switch (action)
        {
            case "attach_vhd": script.Cmd("Add-VMHardDiskDrive", ("VM", VmRef), ("Path", PsValue.Raw(literal))); break;
            case "detach_vhd": script.Line($"Get-VMHardDiskDrive -VM $vm | Where-Object {{ $_.Path -eq {literal} }} | Remove-VMHardDiskDrive"); break;
            case "attach_iso": script.Line($"$dvd = @(Get-VMDvdDrive -VM $vm); if ($dvd.Count -eq 0) {{ Add-VMDvdDrive -VM $vm -Path {literal} }} elseif ($dvd.Count -eq 1) {{ $dvd[0] | Set-VMDvdDrive -Path {literal} }} else {{ Stop-Control 'InvalidArgument' 'Multiple DVD drives; use an exact device configuration' }}"); break;
            case "eject_iso": script.Line("Get-VMDvdDrive -VM $vm | Set-VMDvdDrive -Path $null"); break;
            case "resize_vhd" when sizeGb > 0:
                script.Line($"$disks = @(Get-VMHardDiskDrive -VM $vm | Where-Object {{ $_.Path -eq {literal} }}); if ($disks.Count -ne 1) {{ Stop-Control 'NotFound' 'Disk is not attached exactly once to this VM' }}")
                    .Cmd("Resize-VHD", ("Path", PsValue.Raw(literal)), ("SizeBytes", PsValue.Gigabytes(sizeGb.Value)));
                break;
            default: throw new ArgumentException("Unknown disk action or missing sizeGb.");
        }
        return runner.InvokeAsync(script.Returns(Success), ct, 300);
    }

    public Task<JsonElement> PowerResetAsync(string machine, CancellationToken ct) => runner.InvokeAsync(new HyperVScript().Vm(machine)
        .Cmd("Restart-VM", ("VM", VmRef), ("Force", PsValue.Of(true)), ("Confirm", PsValue.Of(false)))
        .Line(Refresh).Returns(VmProjection), ct);

    public Task<JsonElement> RenameCheckpointAsync(Guid machineId, Guid checkpointId, string newName, CancellationToken ct) => runner.InvokeAsync(new HyperVScript().Vm(machineId)
        .Line($"Get-VMSnapshot -VM $vm | Where-Object {{ $_.Id -eq ([guid]'{checkpointId:D}') }} | Rename-VMSnapshot -NewName {PowerShellRunner.Literal(newName)}")
        .Returns(Success), ct);

    /// <summary>Maps a COM port to a named pipe on an Off VM and reads the port back.</summary>
    public Task<JsonElement> SetComPortAsync(Guid machineId, int number, string pipePath, CancellationToken ct) => runner.InvokeAsync(new HyperVScript().Vm(machineId).RequireOff()
        .Cmd("Set-VMComPort", ("VM", VmRef), ("Number", PsValue.Of(number)), ("Path", PsValue.Of(pipePath)))
        .Returns($"(Get-VMComPort -VM $vm -Number {number} | Select-Object Name,Path)"), ct);
}
