using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HyperVControl.Services;

/// <summary>
/// Elevation on demand for <c>--elevate</c> without administrator rights. This process serves the MCP session itself,
/// so initialize, tools/list and ping never prompt, and runs each tools/call in a same-user worker that the first call
/// starts through UAC. Concurrent first calls share that start, and the worker serves every later call until the
/// session ends: one prompt per session. A declined prompt or a worker that exits becomes a tool error, and the next
/// call starts a worker again.
/// </summary>
internal sealed class ElevationProxy : IAsyncDisposable
{
    /// <summary>
    /// Test seam: <c>always</c> starts the worker with this process's own token instead of UAC (no prompt, no window),
    /// whether or not this process is elevated, so tests exercise the whole proxy path. As a UAC launch would, the
    /// worker starts without the forwarded variables, which therefore still arrive only through the pipe header. The
    /// pipe ACL and both process checks are unchanged; without the variable every worker starts through UAC.
    /// </summary>
    internal const string WorkerVariable = "HYPERV_CONTROL_WORKER";

    internal static bool SeamRequested =>
        string.Equals(Environment.GetEnvironmentVariable(WorkerVariable), "always", StringComparison.OrdinalIgnoreCase);

    internal static string StartupErrorPath => Path.Combine(ControlPaths.DataRoot, "worker-startup-error.txt");

    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly ILoggerFactory? loggerFactory;
    private readonly bool seam;
    private readonly Func<ProcessStartInfo, Process?> launch;
    private readonly string workerPath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    /// <summary>
    /// The current or last start, read and replaced under <see cref="gate"/>. While running, every caller joins it;
    /// once completed it holds the worker in use; a failed start reaches the callers that joined it and the next call
    /// replaces it.
    /// </summary>
    private Task<Worker>? worker;
    private int disposed;

    /// <param name="loggerFactory">Logging for the MCP client that talks to the worker.</param>
    /// <param name="seam">Start the worker without UAC (<see cref="WorkerVariable"/>).</param>
    /// <param name="launch">Starts the worker process; tests replace it. The default is <see cref="Process.Start(ProcessStartInfo)"/>.</param>
    /// <param name="workerPath">The worker executable; the default is this executable.</param>
    internal ElevationProxy(ILoggerFactory? loggerFactory, bool seam, Func<ProcessStartInfo, Process?>? launch = null, string? workerPath = null)
    {
        this.loggerFactory = loggerFactory;
        this.seam = seam;
        this.launch = launch ?? Launch;
        this.workerPath = workerPath ?? Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown.");
    }

    /// <summary>
    /// The tools/call filter: a call to a listed tool runs in the worker. Anything else (an unknown tool name) stays on
    /// the local pipeline, which reports it.
    /// </summary>
    internal McpRequestFilter<CallToolRequestParams, CallToolResult> Filter => next => async (context, ct) =>
        context.MatchedPrimitive is McpServerTool && context.Params is { } call
            ? await CallToolAsync(call.Name, call.Arguments, Progress(context), ct)
            : await next(context, ct);

    /// <summary>Runs one tool call in the worker, starting the worker first when needed, and returns its result unchanged.</summary>
    internal async Task<CallToolResult> CallToolAsync(string name, IDictionary<string, JsonElement>? arguments,
        IProgress<ProgressNotificationValue>? progress, CancellationToken ct)
    {
        using var call = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        Worker current;
        try { current = await AcquireAsync(call.Token); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        { return Failure("ElevationDeclined", "Windows elevation was declined; nothing was changed. Call again to get a new prompt."); }
        catch (WorkerExitedException ex) { return Exited(ex.ExitCode); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return Failure(ex.GetType().Name, $"The elevated worker did not start: {ex.Message} Call again to retry."); }
        try
        {
            return await current.Client.CallToolAsync(name, arguments?.ToDictionary(a => a.Key, a => (object?)a.Value), progress,
                cancellationToken: call.Token);
        }
        catch (Exception ex) when (ex is not McpProtocolException && !call.IsCancellationRequested)
        {
            // The pipe closed under the call. A JSON-RPC error from a live worker propagates unchanged instead.
            if (!await current.EndedAsync()) throw;
            current.MarkReported();
            return Exited(await current.StopAsync());
        }
    }

    /// <summary>The live worker: joins a start in progress or begins one, so concurrent first calls share one prompt.</summary>
    private async Task<Worker> AcquireAsync(CancellationToken ct)
    {
        Task<Worker>? start = null;
        Worker? exited = null;
        await gate.WaitAsync(ct);
        try
        {
            if (worker is { IsCompletedSuccessfully: true } started && !started.Result.Alive)
            {
                // A worker that died between calls is reported to one call instead of prompting at once; the call
                // after that starts a new worker.
                worker = null;
                if (started.Result.MarkReported()) exited = started.Result;
            }
            if (exited is null)
            {
                if (worker is null || worker.IsFaulted || worker.IsCanceled) worker = StartAsync();
                start = worker;
            }
        }
        finally { gate.Release(); }
        if (exited is not null) throw new WorkerExitedException(await exited.StopAsync());
        return await start!.WaitAsync(ct);
    }

    /// <summary>
    /// Starts and connects one worker: a private pipe that only the current user can open, the worker launched with
    /// the pipe name and this process ID, its process ID verified on connection (it verifies ours and our user), the
    /// environment header, then an MCP client over the pipe. Runs on the session's lifetime, not on any one caller's.
    /// </summary>
    private async Task<Worker> StartAsync()
    {
        var token = lifetime.Token;
        var pipeName = "HyperVControl-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? process = null;
        try
        {
            var start = StartInfo(pipeName);
            // ShellExecute blocks until the UAC prompt is answered. A background STA thread keeps the session serving
            // ping and cancellation meanwhile, and a prompt left open never keeps this process alive.
            process = await OnStaThread(() => launch(start)) ?? throw new InvalidOperationException("Windows did not start the elevated worker.");
            token.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ConnectTimeout);
            var connected = pipe.WaitForConnectionAsync(timeout.Token);
            await Task.WhenAny(connected, process.WaitForExitAsync(timeout.Token));
            if (process.HasExited) throw new WorkerExitedException(process.ExitCode);
            token.ThrowIfCancellationRequested();
            if (!connected.IsCompletedSuccessfully)
                throw new TimeoutException($"The elevated worker did not connect within {ConnectTimeout.TotalSeconds:0} seconds.");
            if (!ElevationBridge.IsPipeClient(pipe, process.Id)) throw new InvalidOperationException("Unexpected elevation bridge client.");
            await ElevationBridge.WriteEnvironmentAsync(pipe, Environment.GetEnvironmentVariables(), timeout.Token);
            var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe, loggerFactory), ClientOptions(), loggerFactory, timeout.Token);
            return new Worker(process, pipe, client);
        }
        catch (Exception ex)
        {
            // After the launch, most failures (a broken pipe, a refused invocation) are the worker exiting: report that.
            var exited = ex is not WorkerExitedException && process is not null && await ExitedWithinAsync(process, TimeSpan.FromSeconds(1));
            await pipe.DisposeAsync();
            var exitCode = process is null ? null : await StopAsync(process);
            if (exited) throw new WorkerExitedException(exitCode);
            throw;
        }
    }

    private ProcessStartInfo StartInfo(string pipeName)
    {
        var start = seam
            ? new ProcessStartInfo(workerPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
            : new ProcessStartInfo(workerPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "--worker", pipeName, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), seam ? "seam" : "runas" })
            start.ArgumentList.Add(argument);
        if (seam)
        {
            // A UAC launch starts from the profile environment, not this one: drop the forwarded variables so that, as
            // there, they reach the worker only through the header.
            foreach (var name in start.Environment.Keys.Where(ElevationBridge.IsForwarded).ToList()) start.Environment.Remove(name);
        }
        return start;
    }

    /// <summary>
    /// The default launcher. A seam worker's console output must never reach this process's MCP stdout, so it is
    /// relayed to stderr, where this process logs too. The streams are read directly rather than through
    /// BeginOutputReadLine, so that waiting for the worker's exit does not also wait for their end.
    /// </summary>
    private static Process? Launch(ProcessStartInfo start)
    {
        var process = Process.Start(start);
        if (process is not null && start.RedirectStandardError)
        {
            process.StandardInput.Close();
            _ = RelayAsync(process.StandardOutput);
            _ = RelayAsync(process.StandardError);
        }
        return process;
    }

    private static async Task RelayAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync() is { } line) Console.Error.WriteLine(line); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private static Task<T> OnStaThread<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(action()); }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true, Name = "HyperVControl worker launch" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    /// <summary>
    /// The link to the worker uses the initialize handshake. On the 2026-07-28 revision every result would carry the
    /// worker's per-request server metadata to the client, and the first call would wait for a discovery probe.
    /// </summary>
    private static McpClientOptions ClientOptions() => new()
    {
        ClientInfo = new() { Name = "hyper-v-control-elevation", Version = Program.Version },
        ProtocolVersion = "2025-11-25"
    };

    private static IProgress<ProgressNotificationValue>? Progress(RequestContext<CallToolRequestParams> context) =>
        context.Params?.ProgressToken is { } token ? new ForwardedProgress(context.Server, token) : null;

    private static CallToolResult Failure(string errorType, string error) => HyperVTools.Result(new { ok = false, error, errorType });

    private static CallToolResult Exited(int? exitCode) => Failure("ElevatedWorkerExited",
        $"The elevated worker exited with exit code {exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; see {StartupErrorPath}; call again to restart it.");

    private static async Task<bool> ExitedWithinAsync(Process process, TimeSpan timeout)
    {
        try { await process.WaitForExitAsync().WaitAsync(timeout); return true; }
        catch (TimeoutException) { return false; }
    }

    /// <summary>Waits up to 5 s for a worker whose pipe is closed to exit, then kills it; returns its exit code when known.</summary>
    private static async Task<int?> StopAsync(Process process)
    {
        if (!await ExitedWithinAsync(process, StopTimeout))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception)
            {
                // A worker started through UAC can refuse parts of a tree kill from this unelevated process.
                try { process.Kill(); } catch (Exception) { }
            }
            await ExitedWithinAsync(process, TimeSpan.FromSeconds(2));
        }
        return process.HasExited ? process.ExitCode : null;
    }

    /// <summary>Ends the session's worker: closing its client and pipe ends its session, and it is killed after 5 s.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // Cancelling first also cancels calls still in flight, which tells the worker to cancel them as well.
        await lifetime.CancelAsync();
        Task<Worker>? last;
        await gate.WaitAsync();
        try { (last, worker) = (worker, null); }
        finally { gate.Release(); }
        if (last is null) return;
        // A start still waiting for its UAC prompt cannot be cancelled; it stops its own worker once the prompt returns.
        try { await last.WaitAsync(StopTimeout); }
        catch (Exception) { return; }
        await last.Result.StopAsync();
    }

    /// <summary>A connected worker: its process, the private pipe and the MCP client over it.</summary>
    private sealed class Worker
    {
        private readonly Process process;
        private readonly NamedPipeServerStream pipe;
        private readonly Lazy<Task<int?>> stopped;
        private int reported;

        public Worker(Process process, NamedPipeServerStream pipe, McpClient client)
        {
            this.process = process;
            this.pipe = pipe;
            Client = client;
            stopped = new(StopCoreAsync);
        }

        public McpClient Client { get; }

        public bool Alive => !stopped.IsValueCreated && !process.HasExited && !Client.Completion.IsCompleted;

        /// <summary>True for the first caller only, so that one exit is reported once.</summary>
        public bool MarkReported() => Interlocked.Exchange(ref reported, 1) == 0;

        /// <summary>After a failed call: waits briefly for the pipe or the process to finish closing; true when the worker is gone.</summary>
        public async Task<bool> EndedAsync()
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await Task.WhenAny(Client.Completion, process.WaitForExitAsync(grace.Token)).WaitAsync(grace.Token); }
            catch (OperationCanceledException) { }
            return !Alive;
        }

        /// <summary>Closes the client and the pipe, then waits for the worker to exit; once, however often it is called.</summary>
        public Task<int?> StopAsync() => stopped.Value;

        private async Task<int?> StopCoreAsync()
        {
            await Client.DisposeAsync();
            await pipe.DisposeAsync();
            return await ElevationProxy.StopAsync(process);
        }
    }

    /// <summary>Relays the worker's progress notifications to the caller's own progress token.</summary>
    private sealed class ForwardedProgress(McpServer server, ProgressToken token) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) => _ = server.NotifyProgressAsync(token, value);
    }

    private sealed class WorkerExitedException(int? exitCode) : Exception("The elevated worker exited.")
    {
        public int? ExitCode { get; } = exitCode;
    }
}
