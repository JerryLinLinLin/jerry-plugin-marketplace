using System.Collections.Concurrent;
using HyperVControl.Extracted;

namespace HyperVControl.Services;

internal sealed class ConsoleService(CredentialStore credentials) : IDisposable
{
    private readonly Lazy<HyperVConsoleManager> manager = new(() => new());
    private readonly ConcurrentDictionary<Guid, HyperVConsoleOperationResult> sessions = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    private readonly ConcurrentDictionary<Guid, FileStream> leases = new();
    private const string Owner = "stdio";

    public object List() => sessions.Select(x => new { machineId = x.Key, sessionMode = x.Value.IsEnhanced ? "enhanced" : "basic", x.Value.Note }).ToArray();
    public async Task<object> OpenAsync(Guid id, string mode, int scale, CancellationToken ct)
    {
        if (mode is not ("basic" or "enhanced" or "default")) throw new ArgumentException("session must be basic, enhanced or default.");
        if (scale is not (0 or 100 or 125 or 150 or 175 or 200 or 250 or 300 or 400 or 500)) throw new ArgumentException("Unsupported display scale.");
        var gate = gates.GetOrAdd(id, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!leases.ContainsKey(id)) leases[id] = VmLease.Acquire(id, "console");
            var result = await manager.Value.OpenAsync(Owner, id, 0, Environment.MachineName, mode == "enhanced", scale, ct);
            if (!result.Success) { sessions.TryRemove(id, out _); if(leases.TryRemove(id,out var lease)) lease.Dispose(); throw new InvalidOperationException(result.Error); }
            sessions[id] = result;
            return new { success = true, machineId = id, sessionMode = result.IsEnhanced ? "enhanced" : "basic", result.Note };
        }
        catch
        {
            if (manager.IsValueCreated) await manager.Value.CloseAsync(Owner, id, CancellationToken.None);
            sessions.TryRemove(id, out _);
            if (leases.TryRemove(id, out var lease)) lease.Dispose();
            throw;
        }
        finally { gate.Release(); }
    }
    public async Task<object> CloseAsync(Guid id, CancellationToken ct)
    {
        var gate = gates.GetOrAdd(id, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!manager.IsValueCreated) return new { success = true };
            var result = await manager.Value.CloseAsync(Owner, id, ct);
            sessions.TryRemove(id, out _);
            if (leases.TryRemove(id, out var lease)) lease.Dispose();
            return result;
        }
        finally { gate.Release(); }
    }
    public async Task<HyperVConsoleCommandResult> ExecuteAsync(Guid id, HyperVConsoleAction action, int? x, int? y,
        string? text, int? delta, string profile, CancellationToken ct)
    {
        if (!sessions.ContainsKey(id)) throw new InvalidOperationException("Open a console for this VM first with hyperv_console.");
        if (action is HyperVConsoleAction.Click or HyperVConsoleAction.Scroll or HyperVConsoleAction.Move or HyperVConsoleAction.ButtonDown or HyperVConsoleAction.ButtonUp)
            if (x is null || y is null || x < 0 || y < 0) throw new ArgumentException("Nonnegative screenshot x and y are required.");
        if (action == HyperVConsoleAction.Scroll && (delta is null || delta is < -32768 or > 32767)) throw new ArgumentException("delta must be -32768..32767 (120 per wheel notch).");
        if (text?.Length > 32768) throw new ArgumentException("Input exceeds 32,768 characters.");
        var c = action == HyperVConsoleAction.SignIn ? credentials.Resolve(id, profile) : null;
        var gate = gates.GetOrAdd(id, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var result = await manager.Value.ExecuteAsync(Owner, id, action, x, y, text, delta, c?.UserName, c?.Password, ct);
            if (!result.Success) throw new InvalidOperationException(result.Error);
            return result;
        }
        finally { gate.Release(); }
    }
    public void Dispose()
    {
        if (manager.IsValueCreated) manager.Value.Dispose();
        foreach (var lease in leases.Values) lease.Dispose();
        leases.Clear();
    }
}
