using System.ComponentModel;
using System.Text.Json;
using HyperVControl.Extracted;
using HyperVControl.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HyperVControl;

/// <summary>
/// The MCP contract. Tool methods parse and validate arguments, call one service, and wrap the
/// outcome; no PowerShell text lives in this layer. Every failure uses the same envelope:
/// <c>{ ok:false, error, errorType, ... }</c>, which <see cref="IsFailure"/> maps to an MCP error.
/// </summary>
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
        catch (HyperVOperationException ex) { return Result(new { ok = false, error = ex.Message, errorType = ex.ErrorType, errorId = ex.ErrorId, category = ex.Category }); }
        catch (Exception ex) { return Result(new { ok = false, error = ex.Message, errorType = ex.GetType().Name }); }
    }

    /// <summary>Typed <see cref="HyperVService"/> results share the failure envelope; successes pass through unchanged.</summary>
    internal static object Outcome(bool success, string? error, HyperVCapability capability, object result) => success ? result : new
    {
        ok = false,
        error = error ?? capability.Detail ?? "Hyper-V reported a failure.",
        errorType = capability.IsAvailable ? "HyperVOperationFailed" : capability.State.ToString(),
        result
    };

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
    public Task<CallToolResult> Info(string machine, CancellationToken ct) => Run(async () => await service.Lifecycle.InfoAsync(machine, ct));

    [McpServerTool(Name = "hyperv_power", Destructive = true)]
    [Description("Change VM power: start, shutdown (graceful), restart (graceful stop/wait/start), turn_off (hard), reset (hard), pause, resume, save. No automatic hard-power fallback.")]
    public Task<CallToolResult> Power(string machine, string action, CancellationToken ct) => Run(async () =>
    {
        var vm = await service.ResolveAsync(machine, ct);
        if (action == "reset") return await service.Lifecycle.PowerResetAsync(vm.Id.ToString("D"), ct);
        var op = action switch { "start" => HyperVPowerAction.Start, "shutdown" => HyperVPowerAction.Shutdown,
            "turn_off" => HyperVPowerAction.TurnOff, "restart" => HyperVPowerAction.Restart, "pause" => HyperVPowerAction.Suspend,
            "resume" => HyperVPowerAction.Resume, "save" => HyperVPowerAction.Save, _ => throw new ArgumentException("Unknown power action.") };
        var result = await service.HyperV.ChangePowerStateAsync(vm.Id, op, ct);
        return Outcome(result.Success, result.Error, result.Capability, result);
    });

    [McpServerTool(Name = "hyperv_checkpoints", ReadOnly = true)]
    [Description("List a VM's checkpoint tree with IDs and its current parent checkpoint. Current is not necessarily newest.")]
    public Task<CallToolResult> Checkpoints(string machine, CancellationToken ct) => Run(async () =>
    {
        var result = await service.HyperV.GetCheckpointsAsync((await service.ResolveAsync(machine, ct)).Id, ct);
        return Outcome(result.Success, result.Error, result.Capability, result);
    });

    [McpServerTool(Name = "hyperv_checkpoint", Destructive = true)]
    [Description("Create, restore, remove, or rename one VM checkpoint. Resolve by exact name/ID; 'current' means current parent. Restore discards changes. Deletion does not implicitly include children.")]
    public Task<CallToolResult> Checkpoint(string machine, string action, string checkpoint, string? newName = null, CancellationToken ct = default) => Run(async () =>
    {
        var id = (await service.ResolveAsync(machine, ct)).Id;
        if (action == "create")
        {
            var created = await service.HyperV.CreateCheckpointAsync(id, checkpoint, ct);
            return Outcome(created.Success, created.Error, created.Capability, created);
        }
        var cid = await service.CheckpointIdAsync(id, checkpoint, ct);
        switch (action)
        {
            case "restore":
                var restored = await service.HyperV.RestoreCheckpointAsync(id, cid, ct);
                return Outcome(restored.Success, restored.Error, restored.Capability, restored);
            case "remove":
                var removed = await service.HyperV.DeleteCheckpointAsync(id, cid, ct);
                return Outcome(removed.Success, removed.Error, removed.Capability, removed);
            case "rename" when !string.IsNullOrWhiteSpace(newName):
                return await service.Lifecycle.RenameCheckpointAsync(id, cid, newName, ct);
            default:
                throw new ArgumentException("Use create/restore/remove/rename; rename needs newName.");
        }
    });

    [McpServerTool(Name = "hyperv_firmware", Destructive = true)]
    [Description("Set and verify Secure Boot on an Off Generation 2 VM. Change only for the requested guest task; debugging tools never disable it automatically.")]
    public Task<CallToolResult> Firmware(string machine, bool secureBoot, CancellationToken ct) => Run(async () =>
    {
        var result = await service.HyperV.SetSecureBootAsync((await service.ResolveAsync(machine, ct)).Id, secureBoot, ct);
        return Outcome(result.Success, result.Error, result.Capability, result);
    });
}
