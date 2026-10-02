using System.ComponentModel;
using System.Text.Json;
using HyperVControl.Extracted;
using HyperVControl.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static HyperVControl.Services.ControlService;

namespace HyperVControl;

internal sealed partial class HyperVTools
{
    [McpServerTool(Name = "hyperv_credential", Destructive = true)]
    [Description("Validate and store a VM guest credential in Windows Credential Manager, or remove one. Profiles separate admin and standard-user accounts; no automatic UAC policy changes. Credentials are never returned.")]
    public Task<CallToolResult> Credential(string machine, string action = "set", string profile = "default", string? username = null, string? password = null, CancellationToken ct = default) => Run(async () =>
    {
        var id = (await service.ResolveAsync(machine, ct)).Id;
        CredentialStore.ValidateProfile(profile);
        if (action == "remove") service.Credentials.Remove(id, profile);
        else if (action == "set" && username != null && password != null)
        {
            await service.Guest.InvokeAsync(id, "'credential verified'", new(username, password), ct);
            service.Credentials.Save(id, profile, username, password);
        }
        else throw new ArgumentException("Use set with username/password, or remove.");
        return new { success = true, machineId = id, profile, action };
    });

    [McpServerTool(Name = "hyperv_guest_run", Destructive = true)]
    [Description("Execute PowerShell in a running Windows guest via PowerShell Direct; no network or console needed. Returns separate stdout/stderr, exitCode, timeout and truncation. Runs with the selected guest account's token, never silently disables UAC.")]
    public Task<CallToolResult> GuestRun(string machine, string script, string credentialProfile = "default", int timeoutSeconds = 60, CancellationToken ct = default) => Run(async () =>
        await service.Guest.RunAsync((await service.ResolveAsync(machine, ct)).Id, script, credentialProfile, timeoutSeconds, ct));

    [McpServerTool(Name = "hyperv_guest_process", Destructive = true)]
    [Description("Execute a guest executable with a true argument array and optional working directory, using Windows command-line escaping. For unprivileged tests select a credential profile containing a standard account.")]
    public Task<CallToolResult> GuestProcess(string machine, string executable, string[]? arguments = null, string? workingDirectory = null, string credentialProfile = "default", int timeoutSeconds = 60, CancellationToken ct = default) => Run(async () =>
    {
        FullPath(executable);
        var script = "$psi=[Diagnostics.ProcessStartInfo]::new(" + L(executable) + ")\n"
            + "$psi.Arguments=" + L(HyperVGuestUiService.BuildWindowsArgumentLine(arguments ?? [])) + "\n"
            + "$psi.UseShellExecute=$false; $psi.CreateNoWindow=$true\n"
            + "$psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true\n"
            + "$psi.StandardOutputEncoding=[Text.Encoding]::UTF8; $psi.StandardErrorEncoding=[Text.Encoding]::UTF8\n"
            + (workingDirectory == null ? "" : "$psi.WorkingDirectory=" + L(FullPath(workingDirectory)) + "\n")
            + "$p=[Diagnostics.Process]::Start($psi); try {$stdout=$p.StandardOutput.ReadToEndAsync(); $stderr=$p.StandardError.ReadToEndAsync(); $p.WaitForExit(); [Console]::Out.Write($stdout.Result); [Console]::Error.Write($stderr.Result); exit $p.ExitCode} finally {$p.Dispose()}";
        return await service.Guest.RunAsync((await service.ResolveAsync(machine, ct)).Id, script, credentialProfile, timeoutSeconds, ct);
    });

    [McpServerTool(Name = "hyperv_copy", Destructive = true)]
    [Description("Copy a file or folder host↔guest with PowerShell Direct. direction is to_guest or from_guest; supports recursive folders and Unicode/space paths. Source and destination must be absolute.")]
    public Task<CallToolResult> Copy(string machine, string direction, string source, string destination, string credentialProfile = "default", CancellationToken ct = default) => Run(async () =>
    {
        if (direction is not ("to_guest" or "from_guest")) throw new ArgumentException("Use to_guest/from_guest.");
        return await service.Guest.CopyAsync((await service.ResolveAsync(machine, ct)).Id, source, destination, direction == "from_guest", credentialProfile, ct);
    });

    [McpServerTool(Name = "hyperv_guest_files", ReadOnly = true)]
    [Description("List a guest directory or read at most 1 MiB as base64. Use hyperv_copy for large files. Includes explicit truncation.")]
    public Task<CallToolResult> GuestFiles(string machine, string path, string action = "list", int maxBytes = 1048576, string credentialProfile = "default", CancellationToken ct = default) => Run(async () =>
    {
        FullPath(path);
        if (maxBytes is < 1 or > 1048576) throw new ArgumentException("maxBytes must be 1..1048576.");
        var script = action switch
        {
            "list" => $"ConvertTo-Json -InputObject @(Get-ChildItem -LiteralPath {L(path)} -Force | Select-Object Name,FullName,Length,PSIsContainer,LastWriteTimeUtc) -Compress",
            "read" => $"$f=[IO.File]::OpenRead({L(path)}); try {{$b=New-Object byte[] {maxBytes}; $n=$f.Read($b,0,$b.Length); @{{contentBase64=[Convert]::ToBase64String($b,0,$n); bytesRead=$n; totalBytes=$f.Length; truncated=$f.Length -gt $n}} | ConvertTo-Json -Compress}} finally {{$f.Dispose()}}",
            _ => throw new ArgumentException("Use list/read.")
        };
        var id = (await service.ResolveAsync(machine, ct)).Id;
        var result = await service.Guest.InvokeAsync(id, script, service.Credentials.Resolve(id, credentialProfile), ct);
        return JsonDocument.Parse(result.GetProperty("output").GetString()!).RootElement.Clone();
    });

    [McpServerTool(Name = "hyperv_console")]
    [Description("Open/close/list/show/resize owned VM console windows. session: default/basic/enhanced; default=basic. Actual connected mode and fallback reason are returned. show brings the VM window forward; resize takes width/height pixels. Basic covers boot/sign-in; Enhanced offers clipboard and dynamic resolution.")]
    public Task<CallToolResult> ConsoleTool(string action = "list", string? machine = null, string session = "default", int displayScalePercent = 100, int width = 1280, int height = 720, CancellationToken ct = default) => Run(async () =>
    {
        if (action == "list") return service.Console.List();
        var vm = await service.ResolveAsync(machine ?? throw new ArgumentException("machine required."), ct);
        if (action == "close") return await service.Console.CloseAsync(vm.Id, ct);
        if (action is "show" or "resize") return await service.Console.ExecuteAsync(vm.Id, action == "show" ? HyperVConsoleAction.Show : HyperVConsoleAction.Resize, width, height, null, null, "default", ct);
        if (action != "open") throw new ArgumentException("Use list/open/close/show/resize.");
        if (vm.State != HyperVMachineState.Running) throw new InvalidOperationException("Start/resume the VM before opening its console.");
        return await service.Console.OpenAsync(vm.Id, session, displayScalePercent, ct);
    });

    [McpServerTool(Name = "hyperv_screenshot", ReadOnly = true)]
    [Description("Return the owned VM console PNG inline with width/height and actual mode. Basic is a headless guest framebuffer capture. Enhanced requires its visible unobscured foreground window; never substitute host pixels. Coordinates match hyperv_input.")]
    public async Task<CallToolResult> Screenshot(string machine, CancellationToken ct)
    {
        try
        {
            var id = (await service.ResolveAsync(machine, ct)).Id;
            var frame = await service.Console.ExecuteAsync(id, HyperVConsoleAction.Capture, null, null, null, null, "default", ct);
            return new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { machineId = id, frame.Width, frame.Height, sessionMode = frame.IsEnhanced ? "enhanced" : "basic" }, Program.Json) },
                ImageContentBlock.FromBytes(frame.Png!, "image/png")] };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result(new { ok = false, error = ex.Message }); }
    }

    [McpServerTool(Name = "hyperv_input", Destructive = true)]
    [Description("Send input to the exact VM console: click, move, button_down, button_up, scroll, type, key, login. x/y are latest screenshot pixels. button=left/right/double for click; left/right/middle for down/up. key takes CTRL+ALT+DELETE or WIN+R etc in text. login enters saved password into an already focused guest password field; inspect first.")]
    public Task<CallToolResult> Input(string machine, string action, int? x = null, int? y = null, string? text = null, string button = "left", int? delta = null, string credentialProfile = "default", CancellationToken ct = default) => Run(async () =>
    {
        var kind = action switch { "click" => HyperVConsoleAction.Click, "move" => HyperVConsoleAction.Move,
            "button_down" => HyperVConsoleAction.ButtonDown, "button_up" => HyperVConsoleAction.ButtonUp,
            "scroll" => HyperVConsoleAction.Scroll, "type" => HyperVConsoleAction.Type,
            "key" => HyperVConsoleAction.Key, "login" => HyperVConsoleAction.SignIn, _ => throw new ArgumentException("Unknown input action.") };
        if (action is "type" or "key" && text is null) throw new ArgumentException("text required.");
        var input = action is "click" or "move" or "button_down" or "button_up" ? button : text;
        var r = await service.Console.ExecuteAsync((await service.ResolveAsync(machine, ct)).Id, kind, x, y, input, delta, credentialProfile, ct);
        return new { r.Success, r.Error };
    });

    [McpServerTool(Name = "hyperv_clipboard", Destructive = true)]
    [Description("Read/write the host clipboard redirected into an owned Enhanced session. Requires Enhanced; includes host clipboard data and is not VM-isolated. To paste, send CTRL+V separately after inspecting focus.")]
    public Task<CallToolResult> Clipboard(string machine, string action, string? text = null, CancellationToken ct = default) => Run(async () =>
    {
        var kind = action switch { "read" => HyperVConsoleAction.ClipboardRead, "write" when text != null => HyperVConsoleAction.ClipboardWrite, _ => throw new ArgumentException("Use read/write; write needs text.") };
        return await service.Console.ExecuteAsync((await service.ResolveAsync(machine, ct)).Id, kind, null, null, text, null, "default", ct);
    });

    [McpServerTool(Name = "hyperv_guest_setup")]
    [Description("Install/verify Microsoft's winapp CLI for guest UI Automation. Host downloads an official release, verifies its published SHA-256, copies through PowerShell Direct; guest needs no network. Uses default guest credential.")]
    public Task<CallToolResult> GuestSetup(string machine, CancellationToken ct) => Run(async () => await service.GuestUi.EnsureWinappAsync((await service.ResolveAsync(machine, ct)).Id, ct));

    [McpServerTool(Name = "hyperv_ui", Destructive = true)]
    [Description("Run winapp UI Automation in the signed-in guest's interactive desktop: status, inspect, search, list-windows, invoke, click, focus, get-value, set-value, get-property, wait-for, scroll. Arguments are a token array. Requires guest_setup and unlocked sign-in; headless PowerShell alone cannot see UI. elevated=true only for elevated guest apps.")]
    public Task<CallToolResult> Ui(string machine, string command, string[]? arguments = null, bool elevated = false, int timeoutSeconds = 30, CancellationToken ct = default) => Run(async () =>
    {
        var id = (await service.ResolveAsync(machine, ct)).Id;
        var gate = service.UiGates.GetOrAdd(id, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            using var lease = VmLease.Acquire(id, "ui");
            return await service.GuestUi.RunGuestUiAsync(id, command, arguments ?? [], TimeSpan.FromSeconds(timeoutSeconds), elevated, ct);
        }
        finally { gate.Release(); }
    });
}
