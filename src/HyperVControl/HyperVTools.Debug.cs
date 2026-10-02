using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyperVControl.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static HyperVControl.Services.ControlService;

namespace HyperVControl;

internal sealed partial class HyperVTools
{
    [McpServerTool(Name = "hyperv_debugger_discover", ReadOnly = true)]
    [Description("Discover installed KD/CDB, preferring current WinDbg MSIX over legacy Windows Kits. Returns exact paths, origins and versions. Explicit override: HYPERV_CONTROL_DEBUGGER_DIR.")]
    public Task<CallToolResult> DebuggerDiscover(CancellationToken ct) => Run(async () => await service.Debugger.DiscoverAsync(ct));

    internal static string CreateKdKey()
    {
        static string Base36(ulong value)
        {
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
            var text = "";
            do { text = digits[(int)(value % 36)] + text; value /= 36; } while (value != 0);
            return text;
        }
        return string.Join('.', Enumerable.Range(0, 4).Select(_ => Base36(BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)))));
    }

    [McpServerTool(Name = "hyperv_kernel_configure", Destructive = true)]
    [Description("Configure guest kernel debugging. action=kdnet requires running guest+hostIp. KDCOM is TWO phases: serial_host while Off maps COM pipe; serial_guest while Running changes guest BCD. status reads BCD; disable turns debugging off. Checks native exit codes; never silently disables Secure Boot/BitLocker. reboot is opt-in.")]
    public Task<CallToolResult> KernelConfigure(string machine, string action, string? hostIp = null, int port = 50000,
        string? key = null, string? pipeName = null, int comPort = 1, bool reboot = false, string credentialProfile = "default", CancellationToken ct = default) => Run(async () =>
    {
        var vm = await service.ResolveAsync(machine, ct);
        if (comPort is not (1 or 2)) throw new ArgumentException("comPort must be 1 or 2.");
        pipeName ??= "HyperVControl-" + vm.Id.ToString("N");
        if (!Regex.IsMatch(pipeName, "^[A-Za-z0-9_.-]{1,100}$")) throw new ArgumentException("pipeName must be a simple pipe name (no path).");
        var pipePath = @"\\.\pipe\" + pipeName;
        var serialAttach = $"com:pipe,port={pipePath},resets=0,reconnect";
        if (action == "serial_host")
        {
            var result = await service.PowerShell.JsonAsync(Vm(vm.Id) + RequireOff + $"Set-VMComPort -VM $vm -Number {comPort} -Path {L(pipePath)}\nGet-VMComPort -VM $vm -Number {comPort} | Select-Object Name,Path | ConvertTo-Json -Compress", ct);
            return new { success = true, phase = "host_serial_configured", result, kernelAttachString = serialAttach,
                next = "Start the VM, wait for PowerShell Direct readiness, then call serial_guest with the same COM port. This step has not configured guest BCD." };
        }
        if (vm.State != Extracted.HyperVMachineState.Running) throw new InvalidOperationException("Guest BCD configuration requires Running. Use serial_host only while Off.");
        string script;
        string? attach = null;
        switch (action)
        {
            case "kdnet":
                if (!IPAddress.TryParse(hostIp, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    throw new ArgumentException("hostIp must be an IPv4 address reachable from the guest's vSwitch.");
                if (port is < 1024 or > 65535) throw new ArgumentException("port must be 1024..65535; use a unique port per VM (typically 50000..50039).");
                key ??= CreateKdKey();
                if (!Regex.IsMatch(key, "^[0-9a-z]{1,13}(\\.[0-9a-z]{1,13}){3}$", RegexOptions.IgnoreCase)) throw new ArgumentException("KDNET key must be four base36 components.");
                script = $"Invoke-Bcd @('/dbgsettings','net','hostip:{address}','port:{port}','key:{key}')\nInvoke-Bcd @('/debug','on')\n";
                attach = $"net:port={port},key={key}"; break;
            case "serial_guest":
                script = $"Invoke-Bcd @('/dbgsettings','serial','debugport:{comPort}','baudrate:115200')\nInvoke-Bcd @('/debug','on')\n";
                attach = serialAttach; break;
            case "disable": script = "Invoke-Bcd @('/debug','off')\n"; break;
            case "status": script = ""; break;
            default: throw new ArgumentException("Use kdnet/serial_host/serial_guest/status/disable.");
        }
        script = """
            function Invoke-Bcd([string[]]$tokens) {
                $o=& "$env:SystemRoot\System32\bcdedit.exe" @tokens 2>&1
                if ($LASTEXITCODE -ne 0) { throw ('bcdedit failed (exit '+$LASTEXITCODE+'): '+($o|Out-String)) }
                $o
            }
            """ + "\n" + script + "Invoke-Bcd @('/dbgsettings')\nInvoke-Bcd @('/enum','{current}')\n";
        if (reboot && action != "status") script += "& shutdown.exe /r /t 3; if ($LASTEXITCODE -ne 0) { throw 'Guest reboot scheduling failed' }\n";
        var r = await service.Guest.InvokeAsync(vm.Id, script, service.Credentials.Resolve(vm.Id, credentialProfile), ct);
        return new { success = true, machineId = vm.Id, action, kernelAttachString = attach,
            rebootScheduled = reboot && action != "status", output = r.GetProperty("output"),
            note = "BCD configuration is not proof of debugger connectivity. Check the adapter, host firewall, guest security prerequisites and debugger output; a reboot is needed for BCD changes." };
    });

    [McpServerTool(Name = "hyperv_debug_open", Destructive = true)]
    [Description("Open a persistent host debugger: kernel with KDNET/KDCOM connect string, dump with host dump path, attach with HOST pid, launch with HOST executable+arguments. For processes inside a VM use hyperv_guest_debug_open. Returns session ID and initial output, not a false 'attached' claim.")]
    public Task<CallToolResult> DebugOpen(string mode, string? connectString = null, int? processId = null, string? path = null,
        string[]? arguments = null, string? debuggerDirectory = null, string? symbolPath = null, CancellationToken ct = default) => Run(async () =>
        await service.Debugger.StartAsync(mode, connectString, processId, path, arguments, debuggerDirectory, ct, symbolPath));

    [McpServerTool(Name = "hyperv_debug_sessions", ReadOnly = true)]
    [Description("List persistent debugger sessions owned by this MCP process. Guest worker sessions are accessed with hyperv_guest_debug.")]
    public Task<CallToolResult> DebugSessions() => Run(() => Task.FromResult<object?>(service.Debugger.List()));

    [McpServerTool(Name = "hyperv_debug_command", Destructive = true)]
    [Description("Execute WinDbg/KD/CDB commands in a persistent host session: breakpoints bp/bu/bl/bc, g/t/p, stack k, registers r, memory db/dq/eb, symbols .sympath/.reload, modules lm, !analyze etc. .shell disabled. A timed-out/continuing command returns pending; poll/break before another command.")]
    public Task<CallToolResult> DebugCommand(string sessionId, string command, int waitMs = 10000, CancellationToken ct = default) => Run(async () => await service.Debugger.CommandAsync(sessionId, command, waitMs, ct));

    [McpServerTool(Name = "hyperv_debug_poll", ReadOnly = true)]
    [Description("Read debugger output after a character cursor without blocking or changing target execution. Reports process exit and pending command state.")]
    public Task<CallToolResult> DebugPoll(string sessionId, long cursor = 0) => Run(() => Task.FromResult<object?>(service.Debugger.Poll(sessionId, cursor)));

    [McpServerTool(Name = "hyperv_debug_break", Destructive = true)]
    [Description("Interrupt target execution with Ctrl+Break sent only to this session's private hidden Windows console. Works while g/step is pending; returns the observed debugger state.")]
    public Task<CallToolResult> DebugBreak(string sessionId, CancellationToken ct) => Run(async () => await service.Debugger.BreakAsync(sessionId, ct));

    [McpServerTool(Name = "hyperv_debug_close", Destructive = true)]
    [Description("Break, detach and close a host debugger session. Does not send q directly to an attached user target (which would terminate it). On detach timeout retain the session for inspection.")]
    public Task<CallToolResult> DebugClose(string sessionId, CancellationToken ct) => Run(async () => await service.Debugger.CloseAsync(sessionId, ct));
}
