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

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] != "--worker")
            { Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false); }
            if (args.Contains("--version")) { Console.WriteLine("Hyper-V Control 0.1.0"); return 0; }
            if (args.Contains("--help"))
            {
                Console.WriteLine("HyperVControl.exe [--elevate]\nC# MCP server over stdio. --elevate uses a same-user UAC worker when necessary.\nNo HTTP listener. Log output goes to stderr. See the bundled skill and README.");
                return 0;
            }
            if (args.Contains("--elevate") && !ElevationBridge.IsAdministrator)
            { await ElevationBridge.RunAsync(); return 0; }
            if (args.Length >= 2 && args[0] == "--debug-worker") return await GuestDebugWorker.RunAsync(args[1]);
            if (args.Length >= 2 && args[0] == "--debug-rpc") return await GuestDebugWorker.ClientAsync(args[1]);
            if (args.Length == 1 && args[0] == "--debug-console") return await DebuggerConsole.RunAsync();
            if (args.Length == 2 && args[0] == "--debug-interrupt") return DebuggerConsole.Interrupt(int.Parse(args[1]));

            Stream? workerPipe = null;
            if (args.Length == 3 && args[0] == "--worker")
                workerPipe = await ElevationBridge.ConnectWorkerAsync(args[1], int.Parse(args[2]));
            using var pipeLifetime = workerPipe;
            using var logger = LoggerFactory.Create(b => b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace).SetMinimumLevel(LogLevel.Warning));
            using var control = new ControlService();
            var tools = new HyperVTools(control);
            var options = new McpServerOptions
            {
                ServerInfo = new() { Name = "hyper-v-control", Version = "0.1.0" },
                ServerInstructions = "Manage local Hyper-V VMs by exact name/ID. Start with hyperv_capabilities and hyperv_list. Basic console works at boot without guest credentials; Enhanced requires a supported guest and visible console. PowerShell Direct and UI Automation require guest credentials. Kernel debugger sessions target a VM, never the host kernel. Debugger command timeouts return pending sessions; poll or break them instead of starting another debugger.",
                ToolCollection = []
            };
            foreach (var method in typeof(HyperVTools).GetMethods().Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null))
                options.ToolCollection.Add(McpServerTool.Create(method, tools));
            await using var transport = new StreamServerTransport(workerPipe ?? Console.OpenStandardInput(),
                workerPipe ?? Console.OpenStandardOutput(), "hyper-v-control", logger);
            await using var server = McpServer.Create(transport, options, logger);
            await server.RunAsync();
            return 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { Console.Error.WriteLine("Hyper-V Control: Windows elevation was cancelled. Restart with --elevate when ready, or run with an account granted the necessary Hyper-V rights."); return 1223; }
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
