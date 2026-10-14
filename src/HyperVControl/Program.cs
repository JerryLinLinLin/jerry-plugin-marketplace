using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyperVControl.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HyperVControl;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    /// <summary>The project version; the only place it is read from, for --version and the MCP server info.</summary>
    internal static string Version { get; } = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] != "--worker")
            { Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false); }
            if (args.Contains("--version")) { Console.WriteLine("Hyper-V Control " + Version); return 0; }
            if (args.Contains("--help"))
            {
                Console.WriteLine("HyperVControl.exe [--elevate]\nC# MCP server over stdio. --elevate without administrator rights answers initialize and tools/list unelevated;\nthe first tool call shows one UAC prompt and starts a same-user elevated worker that serves the rest of the session.\nNo HTTP listener. Log output goes to stderr. See the bundled skill and README.");
                return 0;
            }
            if (args.Length >= 2 && args[0] == "--debug-worker") return await GuestDebugWorker.RunAsync(args[1]);
            if (args.Length >= 2 && args[0] == "--debug-rpc") return await GuestDebugWorker.ClientAsync(args[1]);
            if (args.Length == 1 && args[0] == "--debug-console") return await DebuggerConsole.RunAsync();
            if (args.Length == 2 && args[0] == "--debug-interrupt") return DebuggerConsole.Interrupt(int.Parse(args[1]));

            Stream? workerPipe = null;
            if (args.Length > 0 && args[0] == "--worker")
            {
                // --worker <pipe name> <parent process ID> runas|seam, started by ElevationProxy.
                if (args is not [_, var pipeName, var parent, "runas" or "seam"]) throw new InvalidOperationException("Invalid elevated worker invocation.");
                workerPipe = await ElevationBridge.ConnectWorkerAsync(pipeName, int.Parse(parent), seam: args[3] == "seam");
            }
            using var pipeLifetime = workerPipe;
            using var logger = LoggerFactory.Create(b => b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace).SetMinimumLevel(LogLevel.Warning));
            using var control = new ControlService();
            var tools = new HyperVTools(control);
            // --elevate without administrator rights (or through the test seam): this process serves the session, and
            // each tool call runs in the worker that the first call starts.
            await using var proxy = workerPipe == null && args.Contains("--elevate") && (!ElevationBridge.IsAdministrator || ElevationProxy.SeamRequested)
                ? new ElevationProxy(logger, ElevationProxy.SeamRequested) : null;
            var options = new McpServerOptions
            {
                ServerInfo = new() { Name = "hyper-v-control", Version = Version },
                ServerInstructions = "Manage local Hyper-V VMs by exact name/ID. Start with hyperv_capabilities and hyperv_list. Basic console works at boot without guest credentials; Enhanced requires a supported guest and visible console. PowerShell Direct and UI Automation require guest credentials. Kernel debugger sessions target a VM, never the host kernel. Debugger command timeouts return pending sessions; poll or break them instead of starting another debugger.",
                ToolCollection = []
            };
            foreach (var method in typeof(HyperVTools).GetMethods().Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null))
                options.ToolCollection.Add(McpServerTool.Create(method, tools));
            if (proxy != null) options.Filters.Request.CallToolFilters.Add(proxy.Filter);
            await using var transport = new StreamServerTransport(workerPipe ?? Console.OpenStandardInput(),
                workerPipe ?? Console.OpenStandardOutput(), "hyper-v-control", logger);
            await using var server = McpServer.Create(transport, options, logger);
            var session = server.RunAsync();
            if (proxy != null)
            {
                // EOF from the MCP client ends the session even while calls are in flight; stopping the worker ends them.
                await Task.WhenAny(session, transport.MessageReader.Completion);
                await proxy.DisposeAsync();
            }
            await session;
            return 0;
        }
        catch (Exception ex)
        {
            if (args.Length > 0 && args[0] == "--worker")
            {
                try { Directory.CreateDirectory(ControlPaths.DataRoot); File.WriteAllText(Path.Combine(ControlPaths.DataRoot, "worker-startup-error.txt"), ex.ToString()); } catch { }
            }
            Console.Error.WriteLine("Hyper-V Control: " + ex.Message); return 1;
        }
    }
}
