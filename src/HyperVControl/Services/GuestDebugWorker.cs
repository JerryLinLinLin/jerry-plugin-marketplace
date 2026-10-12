using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HyperVControl.Services;

internal sealed record GuestDebugConfiguration(string WorkerId, string Mode, string DebuggerDirectory, int? ProcessId,
    string? Path, string[]? Arguments, string TaskName);

internal static class GuestDebugWorker
{
    internal static string PipeName(string id) => Regex.IsMatch(id, "^[0-9a-f]{32}$")
        ? "HyperVControl-Debug-" + id : throw new ArgumentException("Invalid guest worker ID.");

    internal static async Task<int> RunAsync(string configPath)
    {
        var config = JsonSerializer.Deserialize<GuestDebugConfiguration>(await File.ReadAllTextAsync(configPath), Program.Json)
            ?? throw new ArgumentException("Invalid debug configuration.");
        File.Delete(configPath);
        using var debugger = new DebuggerService(new());
        using var lifetime = new CancellationTokenSource(TimeSpan.FromHours(1));
        object started;
        try { started = await debugger.StartAsync(config.Mode, null, config.ProcessId, config.Path, config.Arguments, config.DebuggerDirectory, lifetime.Token); }
        catch (Exception ex) { started = new { ok = false, error = ex.Message }; }
        var startup = JsonSerializer.SerializeToElement(started, Program.Json);
        var sessionId = startup.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(PipeName(config.WorkerId), PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(lifetime.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                var line = await reader.ReadLineAsync(requestTimeout.Token);
                if (line is null) continue;
                var close = false;
                object result;
                try
                {
                    var request = JsonDocument.Parse(line).RootElement;
                    var action = request.GetProperty("action").GetString();
                    if (action == "status") result = started;
                    else if (sessionId == null) result = started;
                    else result = action switch
                    {
                        "poll" => debugger.Poll(sessionId, request.TryGetProperty("cursor", out var cursor) ? cursor.GetInt64() : 0),
                        "command" => await debugger.CommandAsync(sessionId, request.GetProperty("command").GetString()!, request.TryGetProperty("waitMs", out var wait) ? wait.GetInt32() : 10000, requestTimeout.Token),
                        "break" => await debugger.BreakAsync(sessionId, requestTimeout.Token),
                        "close" => await debugger.CloseAsync(sessionId, requestTimeout.Token),
                        _ => throw new ArgumentException("Use status/poll/command/break/close.")
                    };
                    close = action == "close";
                }
                catch (Exception ex) { result = new { ok = false, error = ex.Message }; }
                await writer.WriteLineAsync(JsonSerializer.Serialize(result, Program.Json));
                if (close) break;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            try { await new PowerShellRunner().RunAsync("Unregister-ScheduledTask -TaskName " + PowerShellRunner.Literal(config.TaskName) + " -Confirm:$false -ErrorAction SilentlyContinue", CancellationToken.None, 15); }
            catch { /* The task name is also returned so failed cleanup is recoverable. */ }
        }
        return 0;
    }

    internal static async Task<int> ClientAsync(string id)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName(id), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await pipe.ConnectAsync(10000, timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        var request = await Console.In.ReadToEndAsync(timeout.Token);
        await writer.WriteLineAsync(request);
        var response = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Guest debug worker disconnected.");
        Console.WriteLine(response);
        return 0;
    }

    internal static async Task<JsonElement> RemoteAsync(ControlService service, Guid vmId, string workerId, object request, string profile, CancellationToken ct)
    {
        PipeName(workerId);
        var payload = JsonSerializer.Serialize(request, Program.Json);
        var script = $$"""
            $psi=[Diagnostics.ProcessStartInfo]::new('C:\ProgramData\HyperVControl\debug\HyperVControl.exe')
            $psi.Arguments='--debug-rpc {{workerId}}'
            $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
            $psi.RedirectStandardInput=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
            $p=[Diagnostics.Process]::Start($psi)
            $out=$p.StandardOutput.ReadToEndAsync(); $err=$p.StandardError.ReadToEndAsync()
            $p.StandardInput.Write({{PowerShellRunner.DecodeUtf8(payload)}}); $p.StandardInput.Close()
            try {
                if(-not $p.WaitForExit(45000)) { $p.Kill(); throw 'Guest debugger RPC timeout' }
                if($p.ExitCode -ne 0) { throw $err.Result }
                $out.Result
            } finally { $p.Dispose() }
            """;
        var response = await service.Guest.InvokeAsync(vmId, script, service.Credentials.Resolve(vmId, profile), ct, 60);
        return JsonDocument.Parse(response.GetProperty("output").GetString()!).RootElement.Clone();
    }
}
