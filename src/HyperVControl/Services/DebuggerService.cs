using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HyperVControl.Services;

internal sealed record DebuggerInstallation(string Directory, string Source, string? Kd, string? Cdb, string? Version);
internal sealed record DebugOutput(string SessionId, int ProcessId, bool Exited, int? ExitCode, bool Pending, long Cursor, bool Truncated, string Output);

internal sealed class DebuggerService(PowerShellRunner runner) : IDisposable
{
    private readonly ConcurrentDictionary<string, DebugSession> sessions = new();

    public async Task<IReadOnlyList<DebuggerInstallation>> DiscoverAsync(CancellationToken ct)
    {
        var candidates = new List<(string Path, string Source)>();
        var configured = Environment.GetEnvironmentVariable("HYPERV_CONTROL_DEBUGGER_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add((configured, "explicit"));
        {
            var appx = await runner.JsonAsync("ConvertTo-Json -InputObject @(Get-AppxPackage -Name Microsoft.WinDbg | Select-Object -ExpandProperty InstallLocation) -Compress", ct);
            foreach (var entry in appx.EnumerateArray()) if (entry.GetString() is { } path) candidates.Add((Path.Combine(path, "amd64"), "WinDbg MSIX"));
        }
        candidates.Add((Path.Combine(ControlPaths.DataRoot, "tools", "windbg", "amd64"), "portable WinDbg MSIX"));
        candidates.Add((Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinDbg", "amd64"), "WinDbg"));
        candidates.Add((Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Debuggers", "x64"), "Windows Kits (legacy fallback)"));
        var found = new List<DebuggerInstallation>();
        foreach (var (dir, source) in candidates.DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
        {
            var kd = Path.Combine(dir, "kd.exe"); var cdb = Path.Combine(dir, "cdb.exe");
            if (!File.Exists(kd) && !File.Exists(cdb)) continue;
            found.Add(new(dir, source, File.Exists(kd) ? kd : null, File.Exists(cdb) ? cdb : null,
                FileVersionInfo.GetVersionInfo(File.Exists(kd) ? kd : cdb).FileVersion));
        }
        return found;
    }

    public async Task<string> ResolveAsync(string exe, string? explicitDirectory, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            var path = Path.Combine(ControlService.FullPath(explicitDirectory), exe);
            if (!File.Exists(path)) throw new FileNotFoundException($"{exe} is missing from the specified directory.", path);
            return path;
        }
        var found = await DiscoverAsync(ct);
        return (exe == "kd.exe" ? found.Select(x => x.Kd) : found.Select(x => x.Cdb)).FirstOrDefault(x => x != null)
            ?? throw new FileNotFoundException("Install current Microsoft WinDbg or set HYPERV_CONTROL_DEBUGGER_DIR to its amd64 directory. Run hyperv_debugger_discover for diagnostics.");
    }

    public async Task<object> StartAsync(string mode, string? connect, int? pid, string? executable,
        string[]? arguments, string? debuggerDirectory, CancellationToken ct, string? symbolPath = null)
    {
        if (sessions.Count >= 8) throw new InvalidOperationException("Close a debugger session before opening more than eight.");
        var engine = await ResolveAsync(mode == "kernel" ? "kd.exe" : "cdb.exe", debuggerDirectory, ct);
        var args = new List<string> { "-noshell" };
        switch (mode)
        {
            case "kernel":
                if (connect is null || !(connect.StartsWith("net:", StringComparison.OrdinalIgnoreCase) || connect.StartsWith("com:pipe,", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Use a KDNET net: or KDCOM com:pipe, attach string. Local host kernel debugging is not exposed.");
                args.AddRange(["-k", connect]); break;
            case "attach":
                if (pid is null or <= 0) throw new ArgumentException("Positive target PID required.");
                args.AddRange(["-p", pid.Value.ToString()]); break;
            case "launch":
                if (executable is null || !File.Exists(ControlService.FullPath(executable))) throw new ArgumentException("Existing target executable required.");
                args.AddRange(["-noinh", "-2"]); // Keep a console debuggee out of the debugger's interrupt console.
                args.Add(executable); args.AddRange(arguments ?? []); break;
            case "dump":
                if (executable is null || !File.Exists(ControlService.FullPath(executable))) throw new ArgumentException("Existing dump path required.");
                args.AddRange(["-z", executable]); break;
            default: throw new ArgumentException("Mode must be kernel, attach, launch or dump.");
        }
        var session = new DebugSession(engine, args, mode, symbolPath);
        if (!sessions.TryAdd(session.Id, session)) { session.Dispose(); throw new InvalidOperationException("Session collision."); }
        await Task.Delay(300, ct);
        return new { success = !session.Process.HasExited || session.Process.ExitCode == 0, sessionId = session.Id, mode, debugger = engine, version = FileVersionInfo.GetVersionInfo(engine).FileVersion,
            result = session.Read(0), note = "Poll for connection and initial break before commands. A process start is not proof of a debugger attachment." };
    }

    private DebugSession Get(string id) => sessions.TryGetValue(id, out var s) ? s : throw new ArgumentException("Unknown debugger session ID.");
    public object List() => sessions.Values.Select(s => new { sessionId = s.Id, mode = s.Mode, state = s.Read(0) }).ToArray();
    public DebugOutput Poll(string id, long cursor) => Get(id).Read(cursor);
    public Task<DebugOutput> CommandAsync(string id, string command, int waitMs, CancellationToken ct) => Get(id).CommandAsync(command, waitMs, ct);
    public async Task<DebugOutput> BreakAsync(string id, CancellationToken ct)
    {
        var s = Get(id);
        if (s.Process.HasExited) return s.Read(0);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--debug-interrupt");
        start.ArgumentList.Add(s.Process.Id.ToString());
        using var wake = Process.Start(start)!;
        var output = wake.StandardOutput.ReadToEndAsync(); var error = wake.StandardError.ReadToEndAsync();
        wake.StandardInput.Close();
        try { await wake.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct); }
        catch { try { wake.Kill(); } catch (InvalidOperationException) { } throw; }
        if (wake.ExitCode != 0) throw new InvalidOperationException("Debugger console interrupt failed: " + await error + await output);
        var deadline = Stopwatch.StartNew();
        while (s.Read(0).Pending && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(100, ct);
        return s.Read(0);
    }
    public async Task<object> CloseAsync(string id, CancellationToken ct)
    {
        var s = Get(id);
        if (!s.Process.HasExited)
        {
            await BreakAsync(id, ct);
            await s.Process.StandardInput.WriteLineAsync(".detach\nq");
            await s.Process.StandardInput.FlushAsync(ct);
            try { await s.Process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct); }
            catch (TimeoutException) { throw new TimeoutException("Debugger has not detached. Session retained; inspect/poll and retry close."); }
        }
        var final = s.Read(0);
        sessions.TryRemove(id, out _); s.Dispose();
        return new { closed = true, result = final };
    }
    public void Dispose()
    {
        // Interrupt/detach independent sessions together so client EOF cleanup stays bounded.
        Task.WhenAll(sessions.Values.Select(s => Task.Run(s.Dispose))).GetAwaiter().GetResult();
        sessions.Clear();
    }

    private sealed class DebugSession : IDisposable
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Mode { get; }
        public string Engine { get; }
        public Process Process { get; }
        private readonly StringBuilder output = new();
        private readonly object gate = new();
        private readonly SemaphoreSlim commandGate = new(1, 1);
        private long dropped;
        private string? pendingMarker;
        private const int Limit = 1_000_000;

        public DebugSession(string engine, IEnumerable<string> args, string mode, string? symbolPath)
        {
            Mode = mode; Engine = engine;
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(engine)! };
            start.ArgumentList.Add("--debug-console");
            var symbols = symbolPath ?? Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH")
                ?? $"srv*{Path.Combine(ControlPaths.DataRoot, "symbols")}*https://msdl.microsoft.com/download/symbols";
            Process = Process.Start(start) ?? throw new InvalidOperationException("Debugger did not start.");
            Process.StandardInput.WriteLine(JsonSerializer.Serialize(new DebuggerConsoleRequest(engine, args.ToArray(), symbols), Program.Json));
            Process.StandardInput.Flush();
            _ = PumpAsync(Process.StandardOutput); _ = PumpAsync(Process.StandardError);
        }
        private async Task PumpAsync(StreamReader reader)
        {
            var buffer = new char[4096];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer)) > 0)
                {
                    lock (gate)
                    {
                        output.Append(buffer, 0, count);
                        if (pendingMarker != null && Regex.IsMatch(output.ToString(), "(?m)^(?:[^\\r\\n>]{1,40}>[ \\t]*)?" + Regex.Escape(pendingMarker) + "\\r?$")) pendingMarker = null;
                        if (output.Length > Limit) { var remove = output.Length - Limit; output.Remove(0, remove); dropped += remove; }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
        public DebugOutput Read(long cursor)
        {
            lock (gate)
            {
                var end = dropped + output.Length;
                if (cursor < 0 || cursor > end) throw new ArgumentOutOfRangeException(nameof(cursor));
                var begin = (int)(Math.Max(cursor, dropped) - dropped);
                return new(Id, Process.Id, Process.HasExited, Process.HasExited ? Process.ExitCode : null,
                    !Process.HasExited && pendingMarker != null, end, cursor < dropped, output.ToString(begin, output.Length - begin));
            }
        }
        public async Task<DebugOutput> CommandAsync(string command, int waitMs, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(command) || command.Length > 32768) throw new ArgumentException("Debugger command must contain 1-32768 characters.");
            if (waitMs is < 0 or > 30000) throw new ArgumentException("waitMs must be 0-30000.");
            await commandGate.WaitAsync(ct);
            try
            {
                long cursor;
                lock (gate)
                {
                    if (pendingMarker != null) throw new InvalidOperationException("A debugger command is still pending. Poll or break the target before submitting another command.");
                    if (Process.HasExited) throw new InvalidOperationException("Debugger has exited.");
                    cursor = dropped + output.Length;
                    pendingMarker = "HVC_END_" + Guid.NewGuid().ToString("N");
                }
                var marker = pendingMarker;
                await Process.StandardInput.WriteLineAsync(command + "\n.echo " + marker);
                await Process.StandardInput.FlushAsync(ct);
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < waitMs)
                {
                    var result = Read(cursor);
                    if (!result.Pending || result.Exited) return result;
                    await Task.Delay(50, ct);
                }
                return Read(cursor);
            }
            finally { commandGate.Release(); }
        }
        public void Dispose()
        {
            if (!Process.HasExited)
            {
                try
                {
                    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                    start.ArgumentList.Add("--debug-interrupt"); start.ArgumentList.Add(Process.Id.ToString());
                    using var interrupt = System.Diagnostics.Process.Start(start);
                    if (interrupt is not null && !interrupt.WaitForExit(1500)) interrupt.Kill(true);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                try { Process.StandardInput.WriteLine(".detach\nq"); Process.StandardInput.Flush(); } catch (IOException) { }
                if (!Process.WaitForExit(1500)) { try { Process.Kill(true); } catch (InvalidOperationException) { } }
            }
            Process.Dispose();
        }
    }
}
