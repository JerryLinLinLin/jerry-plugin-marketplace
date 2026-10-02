// Extracted and adapted from RaivenX b5971dda1fa2050dde36b8b22fda9726df92cb48. See THIRD-PARTY-NOTICES.md.
namespace HyperVControl.Extracted;

/// <summary>Whether the current Windows account can manage the local Hyper-V host.</summary>
public enum HyperVCapabilityState
{
    Available,
    NotInstalled,
    AccessDenied,
    Unavailable,
}

/// <summary>
/// Host capability plus a user-facing explanation when it is unavailable, or, when it is
/// available, a note about machines Hyper-V itself could not read.
/// </summary>
public sealed record HyperVCapability(HyperVCapabilityState State, string? Detail)
{
    public bool IsAvailable => State == HyperVCapabilityState.Available;
}

/// <summary>A normalized subset of the states exposed by the Hyper-V PowerShell module.</summary>
public enum HyperVMachineState
{
    Unknown,
    Off,
    Running,
    Paused,
    Saved,
    Starting,
    Stopping,
    Saving,
    Pausing,
    Resuming,
    Reset,
    FastSaved,
    FastSaving,
    Critical,
}

/// <summary>Read-only inventory data for one local Hyper-V virtual machine.</summary>
public sealed record HyperVMachine(
    Guid Id,
    string Name,
    HyperVMachineState State,
    int? Generation,
    long? MemoryAssignedBytes,
    int? ProcessorCount,
    TimeSpan? Uptime,
    string? OperatingSystem,
    string? Version);

/// <summary>A point-in-time view of the local Hyper-V host.</summary>
public sealed record HyperVInventory(
    string HostName,
    HyperVCapability Capability,
    IReadOnlyList<HyperVMachine> Machines);

/// <summary>Explicit lifecycle transitions supported for a local Hyper-V virtual machine.</summary>
public enum HyperVPowerAction
{
    Start,
    Shutdown,
    TurnOff,
    Restart,
    Suspend,
    Resume,
    Save,
}

/// <summary>Result of one requested power transition.</summary>
public sealed record HyperVPowerOperationResult(
    bool Success,
    HyperVCapability Capability,
    HyperVMachine? Machine,
    string? Error);

/// <summary>Result of changing one Generation 2 virtual machine's firmware.</summary>
public sealed record HyperVFirmwareOperationResult(
    bool Success,
    HyperVCapability Capability,
    HyperVMachine? Machine,
    bool? SecureBootEnabled,
    string? Error);

/// <summary>Read-only metadata for one Hyper-V checkpoint.</summary>
public sealed record HyperVCheckpoint(
    Guid Id,
    Guid MachineId,
    string Name,
    DateTimeOffset CreationTime,
    Guid? ParentCheckpointId,
    string? Type);

/// <summary>Result of listing the checkpoints that belong to one virtual machine.</summary>
public sealed record HyperVCheckpointListResult(
    bool Success,
    HyperVCapability Capability,
    IReadOnlyList<HyperVCheckpoint> Checkpoints,
    string? Error,
    Guid? CurrentParentCheckpointId = null);

/// <summary>Result of creating, restoring, or deleting one virtual machine checkpoint.</summary>
public sealed record HyperVCheckpointOperationResult(
    bool Success,
    HyperVCapability Capability,
    HyperVCheckpoint? Checkpoint,
    string? Error);

/// <summary>Result of attaching an elevated Basic VM Console to an app-owned host window.</summary>
/// <param name="IsEnhanced">
/// The session mode the console ACTUALLY connected with. The app requests a mode, but the
/// broker silently falls back to Basic when Enhanced Session is unavailable — and the two
/// modes have different capture semantics (Basic reads the guest framebuffer headlessly;
/// Enhanced copies real screen pixels and needs the console visible), so the app must know
/// which one it got.
/// </param>
/// <param name="IsEnhanced">The session type the console actually connected with.</param>
/// <param name="Note">
/// What a successful open still wants said — an Enhanced Session that fell back to Basic
/// names why here, so the agent and the user learn it without a failure.
/// </param>
public sealed record HyperVConsoleOperationResult(
    bool Success,
    string? Error,
    bool IsEnhanced = false,
    string? Note = null);

/// <summary>One operation performed against the console currently attached to a chat.</summary>
public enum HyperVConsoleAction
{
    Capture,
    Click,
    Type,
    Key,
    Scroll,
    SignIn,
    Move,
    ButtonDown,
    ButtonUp,
    ClipboardRead,
    ClipboardWrite,
    Show,
    Resize,
}

/// <summary>
/// Result returned by an elevated console operation. Capture is the only action that
/// populates the PNG, dimensions, and session mode.
/// </summary>
public sealed record HyperVConsoleCommandResult(
    bool Success,
    string? Error,
    byte[]? Png = null,
    int? Width = null,
    int? Height = null,
    bool IsEnhanced = false,
    string? Text = null);

/// <summary>Output from one PowerShell Direct command executed inside a Windows guest.</summary>
public sealed record HyperVGuestCommandResult(
    bool Success,
    string? Output,
    string? Error);
