using System.Text.Json;
using HyperVControl.Extracted;

namespace HyperVControl.Services;

internal sealed class ControlService : IDisposable
{
    public PowerShellRunner PowerShell { get; } = new();
    public CredentialStore Credentials { get; } = new();
    public HyperVService HyperV { get; }
    public GuestService Guest { get; }
    public DebuggerService Debugger { get; }
    public ConsoleService Console { get; }
    public HyperVGuestUiService GuestUi { get; }
    // One UI task per VM is serialized; different VMs remain independent.
    internal readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> UiGates = new();

    public ControlService()
    {
        HyperV = new HyperVService(async (script, ct) =>
        {
            var result = await PowerShell.RunAsync(script, ct, 360);
            return new(result.ExitCode, result.Stdout, result.Stderr);
        });
        Guest = new(PowerShell, Credentials);
        Debugger = new(PowerShell);
        Console = new(Credentials);
        GuestUi = new HyperVGuestUiService(
            (id, script, ct) => Guest.RunTextAsync(id, script, "default", ct),
            async (id, archive, ct) =>
            {
                var c = Credentials.Resolve(id);
                var script = HyperVGuestUiService.BuildHostCopyScript(id, c.UserName, c.Password, archive);
                var result = await PowerShell.JsonAsync(script, ct, 300);
                return result.GetProperty("success").GetBoolean()
                    ? new(true, result.GetProperty("exePath").GetString(), null)
                    : new(false, null, result.GetProperty("error").GetString());
            });
    }

    public async Task<HyperVMachine> ResolveAsync(string machine, CancellationToken ct)
    {
        var inventory = await HyperV.GetInventoryAsync(ct);
        if (!inventory.Capability.IsAvailable) throw new InvalidOperationException(inventory.Capability.Detail ?? inventory.Capability.State.ToString());
        var matches = inventory.Machines.Where(vm => vm.Id.ToString().Equals(machine, StringComparison.OrdinalIgnoreCase)
            || vm.Name.Equals(machine, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"VM '{machine}' not found. Call hyperv_list to get exact names/IDs."),
            _ => throw new ArgumentException("VM name is ambiguous. Use its ID.")
        };
    }

    internal static string Vm(Guid id) => $"$vm = Get-VM -Id ([guid]'{id:D}') -ErrorAction Stop\n";
    internal static string VmJson => "$vm | Select-Object Id,Name,State,Generation,ProcessorCount,MemoryStartup,MemoryAssigned,DynamicMemoryEnabled,CheckpointType,Path | ConvertTo-Json -Depth 6 -Compress";
    internal static string RequireOff => "if ([string]$vm.State -ne 'Off') { throw 'This configuration change requires the VM to be Off.' }\n";
    internal static string L(string text) => PowerShellRunner.Literal(text);

    public async Task<JsonElement> InfoAsync(Guid id, CancellationToken ct) => await PowerShell.JsonAsync(Vm(id) + """
        @{ vm=($vm | Select-Object Id,Name,State,Generation,Version,ProcessorCount,MemoryStartup,MemoryAssigned,MemoryMinimum,MemoryMaximum,DynamicMemoryEnabled,CheckpointType,ParentSnapshotId,Path);
           firmware=$(if($vm.Generation -eq 2){ Get-VMFirmware -VM $vm | Select-Object @{Name='SecureBoot';Expression={$_.SecureBoot.ToString()}},SecureBootTemplate,PreferredNetworkBootProtocol }else{$null});
           network=@(Get-VMNetworkAdapter -VM $vm | Select-Object Id,Name,SwitchName,MacAddress,IPAddresses,Status);
           disks=@(Get-VMHardDiskDrive -VM $vm | Select-Object Path,ControllerType,ControllerNumber,ControllerLocation);
           dvd=@(Get-VMDvdDrive -VM $vm | Select-Object Path,ControllerNumber,ControllerLocation);
           com=@(Get-VMComPort -VM $vm | Select-Object Name,Path);
           integration=@(Get-VMIntegrationService -VM $vm | Select-Object Id,Name,Enabled,PrimaryStatusDescription)
        } | ConvertTo-Json -Depth 8 -Compress
        """, ct);

    public async Task<Guid> CheckpointIdAsync(Guid id, string checkpoint, CancellationToken ct)
    {
        var list = await HyperV.GetCheckpointsAsync(id, ct);
        if (!list.Success) throw new InvalidOperationException(list.Error);
        if (checkpoint == "current") return list.CurrentParentCheckpointId ?? throw new InvalidOperationException("VM has no current parent checkpoint.");
        var matches = list.Checkpoints.Where(c => c.Id.ToString().Equals(checkpoint, StringComparison.OrdinalIgnoreCase)
            || c.Name.Equals(checkpoint, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0].Id : throw new ArgumentException("Checkpoint missing or ambiguous; use an ID from hyperv_checkpoints.");
    }

    internal static string FullPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Split('\\', '/').Contains(".."))
            throw new ArgumentException("Use an absolute path without '..' components.");
        return path;
    }

    public void Dispose()
    {
        Console.Dispose(); Debugger.Dispose();
        foreach (var gate in UiGates.Values) gate.Dispose();
    }
}
