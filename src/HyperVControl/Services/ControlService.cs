using System.Text.Json;
using HyperVControl.Extracted;

namespace HyperVControl.Services;

/// <summary>Composition root for the services behind the MCP tools; owns their lifetimes.</summary>
internal sealed class ControlService : IDisposable
{
    public PowerShellRunner PowerShell { get; } = new();
    public CredentialStore Credentials { get; } = new();
    public HyperVService HyperV { get; }
    public VmLifecycleService Lifecycle { get; }
    public JobRunner Jobs { get; } = new();
    public ImageCatalogService Images { get; }
    public ImageBuildService ImageBuild { get; }
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
        Lifecycle = new(PowerShell);
        Images = new(PowerShell, Jobs);
        ImageBuild = new(PowerShell, Jobs);
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

    /// <summary>
    /// Resolves an exact name or GUID through the typed inventory. Tools that need the VM's state
    /// before choosing a channel (console, guest, debugger) use this; host operations resolve inside
    /// their own script instead, through <see cref="HyperVScript.Vm(string)"/>.
    /// </summary>
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

    /// <summary>Single-quoted PowerShell literal for guest-side scripts.</summary>
    internal static string L(string text) => PowerShellRunner.Literal(text);

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
        Console.Dispose(); Debugger.Dispose(); Jobs.Dispose();
        foreach (var gate in UiGates.Values) gate.Dispose();
    }
}
