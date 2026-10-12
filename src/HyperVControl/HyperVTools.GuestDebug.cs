using System.ComponentModel;
using System.Text.Json;
using HyperVControl.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static HyperVControl.Services.ControlService;

namespace HyperVControl;

internal sealed partial class HyperVTools
{
    internal sealed record GuestDebugStartup(string WorkerId, string TaskName, JsonElement Result)
    {
        public bool Success => !IsFailure(Result);
    }

    [McpServerTool(Name = "hyperv_guest_debug_setup")]
    [Description("Deploy this published self-contained EXE and the selected host WinDbg amd64 engine directory into C:\\ProgramData\\HyperVControl\\debug in a Windows VM via PowerShell Direct. Enables persistent in-guest CDB without network or Python. Requires a guest admin credential.")]
    public Task<CallToolResult> GuestDebugSetup(string machine, string? debuggerDirectory = null, string credentialProfile = "default", CancellationToken ct = default) => Run(async () =>
    {
        var id = (await service.ResolveAsync(machine, ct)).Id;
#pragma warning disable IL3000 // Deliberately distinguishes development output from the single-file worker.
        if (!string.IsNullOrEmpty(typeof(Program).Assembly.Location))
            throw new InvalidOperationException("Guest worker deployment requires the published single-file EXE; run scripts/build-hyperv-control.ps1 first.");
#pragma warning restore IL3000
        var engine = await service.Debugger.ResolveAsync("cdb.exe", debuggerDirectory, ct);
        var directory = Path.GetDirectoryName(engine)!;
        await service.Guest.CopyAsync(id, Environment.ProcessPath!, @"C:\ProgramData\HyperVControl\debug\HyperVControl.exe", false, credentialProfile, ct);
        await service.Guest.InvokeAsync(id, "New-Item -ItemType Directory -Force -Path 'C:\\ProgramData\\HyperVControl\\debug\\engines' | Out-Null", service.Credentials.Resolve(id, credentialProfile), ct);
        // Copy the directory into a parent; Copy-Item preserves the final directory name.
        await service.Guest.CopyAsync(id, directory, @"C:\ProgramData\HyperVControl\debug\engines\", false, credentialProfile, ct);
        return new { success = true, guestExecutable = @"C:\ProgramData\HyperVControl\debug\HyperVControl.exe",
            guestDebuggerDirectory = @"C:\ProgramData\HyperVControl\debug\engines\" + Path.GetFileName(directory) };
    });

    [McpServerTool(Name = "hyperv_guest_debug_open", Destructive = true)]
    [Description("Launch or attach CDB INSIDE a VM (mode=launch/attach/dump). guestProcessId and path refer to the guest. First deploy using guest_debug_setup. A bounded one-hour S4U scheduled worker survives PowerShell Direct disconnects; use guest_debug close when finished. Guest admin credentials required to register it.")]
    public Task<CallToolResult> GuestDebugOpen(string machine, string mode, string guestDebuggerDirectory,
        int? guestProcessId = null, string? path = null, string[]? arguments = null, string credentialProfile = "default", CancellationToken ct = default) => Run(async () =>
    {
        if (mode is not ("launch" or "attach" or "dump")) throw new ArgumentException("Use launch/attach/dump.");
        FullPath(guestDebuggerDirectory);
        var vmId = (await service.ResolveAsync(machine, ct)).Id;
        var worker = Guid.NewGuid().ToString("N");
        var task = "HyperVControl-Debug-" + worker;
        var config = new GuestDebugConfiguration(worker, mode, guestDebuggerDirectory, guestProcessId, path, arguments, task);
        var encoded = PowerShellRunner.DecodeUtf8(JsonSerializer.Serialize(config, Program.Json));
        var script = $$"""
            $exe='C:\ProgramData\HyperVControl\debug\HyperVControl.exe'
            if(-not (Test-Path -LiteralPath $exe)) { throw 'Run hyperv_guest_debug_setup first.' }
            $configPath='C:\ProgramData\HyperVControl\debug\{{worker}}.json'
            [IO.File]::WriteAllText($configPath, {{encoded}}, [Text.UTF8Encoding]::new($false))
            $principal=New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) -LogonType S4U -RunLevel Highest
            $action=New-ScheduledTaskAction -Execute $exe -Argument ('--debug-worker "'+$configPath+'"')
            $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 65) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
            Register-ScheduledTask -TaskName '{{task}}' -Action $action -Principal $principal -Settings $settings -Force | Out-Null
            Start-ScheduledTask -TaskName '{{task}}'
            'worker scheduled'
            """;
        await service.Guest.InvokeAsync(vmId, script, service.Credentials.Resolve(vmId, credentialProfile), ct);
        var state = await GuestDebugWorker.RemoteAsync(service, vmId, worker, new { action = "status" }, credentialProfile, ct);
        return new GuestDebugStartup(worker, task, state);
    });

    [McpServerTool(Name = "hyperv_guest_debug", Destructive = true)]
    [Description("Control a persistent in-guest CDB worker via PowerShell Direct: status, poll(cursor), command(command,waitMs), break, close. For breakpoints, stepping, memory, registers, stack and symbols use normal WinDbg commands. Retains pending commands across calls; no guest TCP service required.")]
    public Task<CallToolResult> GuestDebug(string machine, string workerId, string action, string? command = null,
        int waitMs = 10000, long cursor = 0, string credentialProfile = "default", CancellationToken ct = default) => Run(async () =>
            await GuestDebugWorker.RemoteAsync(service, (await service.ResolveAsync(machine, ct)).Id, workerId, new { action, command, waitMs, cursor }, credentialProfile, ct));
}
