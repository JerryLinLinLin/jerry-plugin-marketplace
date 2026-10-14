using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Text.Json;
using HyperVControl.Services;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace HyperVControl.Tests;

/// <summary>
/// Elevation on demand: <c>--elevate</c> serves the session unelevated and starts one worker on the first tool call.
/// Unit checks replace the launcher (a declined prompt cannot be produced otherwise); smoke checks drive the published
/// EXE through the <c>HYPERV_CONTROL_WORKER=always</c> seam, which starts the worker without a UAC dialog.
/// </summary>
internal static class ElevationProxyTests
{
    private const string Declined = "Windows elevation was declined; nothing was changed. Call again to get a new prompt.";

    internal static async Task UnitAsync(Action<bool, string> check)
    {
        // How the worker is started: the UAC path and the seam differ only there.
        const string marker = "HYPERV_CONTROL_PROXY_UNIT_MARKER";
        ProcessStartInfo? runas = null, seam = null;
        Environment.SetEnvironmentVariable(marker, "forwarded");
        try
        {
            await using (var proxy = new ElevationProxy(null, seam: false, start => { runas = start; throw new Win32Exception(1223); }, "worker.exe"))
                await proxy.CallToolAsync("hyperv_list", null, null, default);
            await using (var proxy = new ElevationProxy(null, seam: true, start => { seam = start; throw new Win32Exception(1223); }, "worker.exe"))
                await proxy.CallToolAsync("hyperv_list", null, null, default);
        }
        finally { Environment.SetEnvironmentVariable(marker, null); }
        check(runas is { UseShellExecute: true, Verb: "runas", WindowStyle: ProcessWindowStyle.Hidden, FileName: "worker.exe" }
            && runas.ArgumentList is ["--worker", var pipe, var parent, "runas"] && pipe.StartsWith("HyperVControl-", StringComparison.Ordinal)
            && parent == Environment.ProcessId.ToString(), "Without the seam the worker starts through UAC (runas, hidden) with its pipe name and this process ID");
        check(seam is { UseShellExecute: false, CreateNoWindow: true, Verb: "", RedirectStandardInput: true, RedirectStandardOutput: true, RedirectStandardError: true }
            && seam.ArgumentList is ["--worker", _, _, "seam"] && !seam.Environment.ContainsKey(marker) && seam.Environment.ContainsKey("PATH"),
            "The seam starts the worker without UAC or a window, and without the forwarded variables, which only the pipe header carries");

        var prompts = 0;
        await using (var proxy = new ElevationProxy(null, seam: false, _ => { Interlocked.Increment(ref prompts); throw new Win32Exception(1223); }, "worker.exe"))
        {
            var first = Error(await proxy.CallToolAsync("hyperv_list", null, null, default));
            check(first is ("ElevationDeclined", Declined) && prompts == 1, "A declined UAC prompt is an ElevationDeclined tool error, not an exit");
            var second = Error(await proxy.CallToolAsync("hyperv_list", null, null, default));
            check(second is ("ElevationDeclined", _) && prompts == 2, "The call after a declined prompt prompts again");
        }

        prompts = 0;
        using var answer = new ManualResetEventSlim();
        await using (var proxy = new ElevationProxy(null, seam: false, _ => { Interlocked.Increment(ref prompts); answer.Wait(); throw new Win32Exception(1223); }, "worker.exe"))
        {
            var calls = Enumerable.Range(0, 8).Select(_ => proxy.CallToolAsync("hyperv_list", null, null, default)).ToList();
            await Task.Delay(200);
            answer.Set();
            var results = await Task.WhenAll(calls);
            check(prompts == 1 && results.All(r => Error(r) is ("ElevationDeclined", _)), "Eight concurrent first calls share one prompt and its answer");
        }

        using var open = new ManualResetEventSlim();
        await using (var proxy = new ElevationProxy(null, seam: false, _ => { open.Wait(); throw new Win32Exception(1223); }, "worker.exe"))
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var watch = Stopwatch.StartNew();
            try { await proxy.CallToolAsync("hyperv_list", null, null, cancel.Token); check(false, "A cancelled call returned a result"); }
            catch (OperationCanceledException) { check(watch.Elapsed < TimeSpan.FromSeconds(2), "A call cancelled while its UAC prompt is open ends at once"); }
            open.Set();
        }

        await using (var proxy = new ElevationProxy(null, seam: true,
            _ => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c exit 7") { UseShellExecute = false, CreateNoWindow = true }), "worker.exe"))
        {
            var exited = Error(await proxy.CallToolAsync("hyperv_list", null, null, default));
            check(exited is ("ElevatedWorkerExited", var message) && message.Contains("exit code 7;") && message.Contains(ElevationProxy.StartupErrorPath)
                && message.EndsWith("call again to restart it."), "A worker that exits before connecting is reported with its exit code and startup log");
        }

        if (!ElevationBridge.IsAdministrator)
        {
            try { await ElevationBridge.ConnectWorkerAsync("HyperVControl-" + Guid.NewGuid().ToString("N"), Environment.ProcessId, seam: false); check(false, "A non-elevated UAC worker is refused"); }
            catch (InvalidOperationException ex) { check(ex.Message == "Invalid elevated worker invocation.", "A non-elevated worker is refused unless its parent started it through the seam"); }
        }
        try { await ElevationBridge.ConnectWorkerAsync("Other-" + Guid.NewGuid().ToString("N"), Environment.ProcessId, seam: true); check(false, "Seam worker pipe name check"); }
        catch (InvalidOperationException ex) { check(ex.Message == "Invalid elevated worker invocation.", "The seam keeps the worker's pipe name check"); }
    }

    internal static async Task SmokeAsync(string executable, string results, Action<bool, string> check)
    {
        var root = Path.Combine(results, "elevation-proxy");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var images = Path.Combine(root, "images 空间");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        // Set only in the server's environment, as an MCP client's configuration would; the worker starts without it.
        var environment = new Dictionary<string, string> { ["HYPERV_CONTROL_IMAGE_ROOT"] = images, ["HYPERV_CONTROL_DATA"] = data };

        await using (var server = await Server.StartAsync(executable, environment))
        {
            var tools = await server.Client.ListToolsAsync();
            await server.Client.PingAsync();
            check(tools.Count >= 30 && server.Workers().Count == 0, $"initialize, tools/list ({tools.Count} tools) and ping start no worker");
            bool rejected;
            try { rejected = (await server.Client.CallToolAsync("hyperv_no_such_tool", new Dictionary<string, object?>())).IsError == true; }
            catch (McpProtocolException) { rejected = true; }
            check(rejected && server.Workers().Count == 0, "An unknown tool name is rejected locally without starting a worker");

            var first = await server.ProcessAsync();
            check(first.Mode == "worker-seam" && first.Id != server.Process.Id && first.Elevated == ElevationBridge.IsAdministrator && first.Unchanged,
                "The first tool call is answered by the seam worker, not by the server, and its result is forwarded unchanged");
            check(server.Workers().SequenceEqual([first.Id]), "The first tool call starts exactly one worker, a child of the server");
            var second = await server.ProcessAsync();
            check(second.Id == first.Id && server.Workers().SequenceEqual([first.Id]), "The next call is answered by the same worker");

            var catalogs = Json(await server.Client.CallToolAsync("hyperv_image", new Dictionary<string, object?> { ["action"] = "catalogs" }));
            check(catalogs.GetProperty("file").GetString() == Path.Combine(images, "catalogs.json"),
                "HYPERV_CONTROL_IMAGE_ROOT set only for the server reaches the worker through the pipe header");

            using (var worker = Process.GetProcessById(first.Id)) { worker.Kill(); await worker.WaitForExitAsync(); }
            var lost = await server.Client.CallToolAsync("hyperv_capabilities", new Dictionary<string, object?>());
            check(Error(lost) is ("ElevatedWorkerExited", var message) && message.Contains("exit code -1;")
                && message.Contains(Path.Combine(data, "worker-startup-error.txt")) && message.EndsWith("call again to restart it.") && server.Workers().Count == 0,
                "A worker killed between calls is reported once as ElevatedWorkerExited, without starting another");
            var restarted = await server.ProcessAsync();
            check(restarted.Mode == "worker-seam" && restarted.Id != first.Id && server.Workers().SequenceEqual([restarted.Id]),
                "The call after that starts a new worker");

            var elapsed = await server.EndAsync(restarted.Id, () => server.Process.StandardInput.Close());
            check(elapsed < TimeSpan.FromSeconds(5) && server.Process.ExitCode == 0, $"Closing stdin ends the server and its worker ({elapsed.TotalMilliseconds:0} ms)");
        }

        await using (var server = await Server.StartAsync(executable, environment))
        {
            var calls = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => server.ProcessAsync()));
            var ids = calls.Select(c => c.Id).Distinct().ToList();
            check(ids.Count == 1 && calls.All(c => c.Mode == "worker-seam") && server.Workers().SequenceEqual(ids),
                "Ten concurrent first calls start exactly one worker, which answers all of them");
            var elapsed = await server.EndAsync(ids[0], () => server.Process.Kill());
            check(elapsed < TimeSpan.FromSeconds(5), $"A server killed outright takes its worker with it ({elapsed.TotalMilliseconds:0} ms)");
        }
        Directory.Delete(root, true);
    }

    private static JsonElement Json(CallToolResult result) =>
        JsonDocument.Parse(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text))).RootElement.Clone();

    /// <summary>(errorType, error) of a failed call, or null for a success.</summary>
    private static (string Type, string Message)? Error(CallToolResult result)
    {
        if (result.IsError != true) return null;
        var json = Json(result);
        return json.GetProperty("ok").GetBoolean() ? null : (json.GetProperty("errorType").GetString()!, json.GetProperty("error").GetString()!);
    }

    /// <summary>Which process answered a <c>hyperv_capabilities</c> call; <paramref name="Unchanged"/>: nothing beyond the tool's own result came back.</summary>
    private sealed record Answer(int Id, bool Elevated, string Mode, bool Unchanged);

    /// <summary>The server under test: its process, started as an MCP client starts it, and an MCP client over its stdio.</summary>
    private sealed class Server(Process process, McpClient client) : IAsyncDisposable
    {
        public Process Process => process;
        public McpClient Client => client;

        public static async Task<Server> StartAsync(string executable, IReadOnlyDictionary<string, string> environment)
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("--elevate");
            start.Environment[ElevationProxy.WorkerVariable] = "always";
            foreach (var (name, value) in environment) start.Environment[name] = value;
            var process = Process.Start(start) ?? throw new InvalidOperationException("The server did not start.");
            _ = Task.Run(async () => { while (await process.StandardError.ReadLineAsync() is { } line) Console.Error.WriteLine(line); });
            // The initialize handshake and ping, as current MCP clients use them (the 2026-07-28 revision has neither).
            var client = await McpClient.CreateAsync(new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, null),
                new McpClientOptions { ProtocolVersion = "2025-11-25" });
            return new Server(process, client);
        }

        /// <summary>The <c>process</c> block of <c>hyperv_capabilities</c>: which process answered.</summary>
        public async Task<Answer> ProcessAsync()
        {
            var result = await client.CallToolAsync("hyperv_capabilities", new Dictionary<string, object?>());
            if (result.IsError == true) throw new Exception("hyperv_capabilities: " + string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text)));
            var answer = Json(result).GetProperty("process");
            return new(answer.GetProperty("id").GetInt32(), answer.GetProperty("elevated").GetBoolean(), answer.GetProperty("mode").GetString()!,
                result.Meta is null && result.StructuredContent is null && result.Content.Count == 1);
        }

        /// <summary>The server's running <c>--worker</c> children.</summary>
        public List<int> Workers()
        {
            using var search = new ManagementObjectSearcher(
                $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE ParentProcessId = {process.Id} AND Name = 'HyperVControl.exe'");
            using var found = search.Get();
            return found.Cast<ManagementObject>().Where(p => p["CommandLine"] is string line && line.Contains(" --worker "))
                .Select(p => Convert.ToInt32(p["ProcessId"])).Order().ToList();
        }

        /// <summary>Ends the session with <paramref name="end"/> and measures until the server and its worker have both exited.</summary>
        public async Task<TimeSpan> EndAsync(int workerId, Action end)
        {
            using var worker = Process.GetProcessById(workerId);
            var watch = Stopwatch.StartNew();
            end();
            await Task.WhenAll(process.WaitForExitAsync(), worker.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30));
            return watch.Elapsed;
        }

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync();
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            process.Dispose();
        }
    }
}
