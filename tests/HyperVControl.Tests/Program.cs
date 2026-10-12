using System.Text.Json;
using HyperVControl.Extracted;
using HyperVControl.Services;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var root = Path.GetFullPath(args.Length > 1 ? args[1] : ".");
var results = Path.Combine(root, "build", "hyperv-control", "validation");
Directory.CreateDirectory(results);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }

if (args.Length == 0 || args[0] == "unit")
{
    Check(Rgb565Pixels.ToBgra32([0, 0, 255, 255, 0, 248, 224, 7, 31, 0], 5, 1).SequenceEqual(new byte[]
        { 0,0,0,255, 255,255,255,255, 0,0,255,255, 0,255,0,255, 255,0,0,255 }), "RGB565 black/white/RGB channels");
    try { Rgb565Pixels.ToBgra32([0], 1, 1); throw new Exception("accepted short frame"); } catch (ArgumentException) { Console.WriteLine("PASS truncated framebuffer rejected"); }
    Check(Rgb565Pixels.ToBgra32([255,255,0,0,0,0], 1, 1).SequenceEqual(new byte[] {255,255,255,255}), "Hyper-V framebuffer trailer tolerated");
    Check(PowerShellRunner.Literal("x'; $pwn = 1; '") == "'x''; $pwn = 1; '''", "PowerShell data quoting");
    var runner = new PowerShellRunner();
    var value = "空间 ' quoted ; $(Write-Output PWN) ` literal";
    var roundtrip = await runner.JsonAsync("@{v=" + PowerShellRunner.Literal(value) + "} | ConvertTo-Json -Compress", default);
    Check(roundtrip.GetProperty("v").GetString() == value, "PowerShell Unicode/injection roundtrip");
    try { await runner.RunAsync("Start-Sleep -Seconds 30", default, 1); throw new Exception("no timeout"); } catch (TimeoutException) { Console.WriteLine("PASS child process timeout"); }
    Check(HyperVGuestUiService.ValidateUiCommand("inspect") == "inspect", "UI inspection routing");
    try { HyperVGuestUiService.ValidateUiCommand("send-keys"); throw new Exception("accepted unsupported UI input"); } catch (ArgumentException) { Console.WriteLine("PASS UI keyboard redirects to console"); }
    Check(HyperVGuestUiService.BuildWindowsArgumentLine(["one two", "a\"b", @"C:\tail space\"]).Contains("\\\""), "Windows argv quote escaping");
    try { await HyperVControl.Tests.ReviewRegressionTests.RunAsync(Check); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; return; }
    Console.WriteLine("UNIT PASS");
    return;
}

var executable = Path.Combine(root, "build", "hyperv-control", "publish", "HyperVControl.exe");
var elevated = args[0] != "smoke";
var launcher = args[0] == "launcher";
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Command = launcher ? "powershell.exe" : executable,
    Arguments = launcher ? ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root,"plugins","hyper-v-control","scripts","start.ps1")] : elevated ? ["--elevate"] : [],
    EnvironmentVariables = new Dictionary<string, string?> { ["HYPERV_CONTROL_DEBUGGER_DIR"] = Path.Combine(root, "build", "hyperv-control", "windbg", "amd64") },
    StandardErrorLines = line => Console.Error.WriteLine(line)
});
using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(30));
await using var client = await McpClient.CreateAsync(transport, cancellationToken: lifetime.Token);
var tools = await client.ListToolsAsync(cancellationToken: lifetime.Token);
Check(tools.Count >= 30, $"MCP initialize and {tools.Count} tools discovered");
Check(tools.Select(t => t.Name).Distinct().Count() == tools.Count, "Unique tool names");
await File.WriteAllTextAsync(Path.Combine(results, "tools.json"), JsonSerializer.Serialize(tools.Select(t => new {t.Name,t.Description,t.JsonSchema}), new JsonSerializerOptions {WriteIndented=true}));

async Task<JsonElement> Call(string name, object? arguments = null, bool allowError = false, string? label = null)
{
    var dict = arguments == null ? new Dictionary<string, object?>() : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments))!;
    var result = await client.CallToolAsync(name, dict, cancellationToken: lifetime.Token);
    var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    foreach (var image in result.Content.OfType<ImageContentBlock>())
    {
        var path = Path.Combine(results, (label ?? name) + ".png");
        await File.WriteAllBytesAsync(path, image.DecodedData.ToArray());
        Console.WriteLine("IMAGE " + path);
    }
    await File.WriteAllTextAsync(Path.Combine(results, (label ?? name) + ".json"), text);
    if (result.IsError == true && !allowError) throw new Exception(name + ": " + text);
    Console.WriteLine((result.IsError == true ? "ERROR " : "PASS ") + (label ?? name) + " " + text[..Math.Min(1600, text.Length)]);
    return JsonDocument.Parse(text).RootElement.Clone();
}

if (args[0] is "smoke" or "launcher")
{
    var invalid = await Call("hyperv_power", new {machine="__missing_hypervcontrol_test_vm__",action="delete_everything"}, true);
    Check(invalid.GetProperty("ok").GetBoolean() == false, "Tool errors are structured MCP errors");
    var paths = await Call("hyperv_vm_create", new {name="not-created",path="relative",vhdPath="relative"}, true);
    Check(paths.GetProperty("ok").GetBoolean() == false, "Invalid path rejected before host mutation");
    Console.WriteLine("STDIO SMOKE PASS");
    return;
}

var machine = Environment.GetEnvironmentVariable("HYPERV_CONTROL_TEST_VM") ?? "Windows 11 AV Test";
if (args[0] == "steps")
{
    using var steps = JsonDocument.Parse(await File.ReadAllTextAsync(args[2]));
    foreach(var step in steps.RootElement.EnumerateArray())
    {
        if(step.TryGetProperty("delayMs",out var delay)) { await Task.Delay(delay.GetInt32()); continue; }
        await Call(step.GetProperty("name").GetString()!,step.GetProperty("arguments"),
            step.TryGetProperty("allowError",out var ae)&&ae.GetBoolean(),step.TryGetProperty("label",out var l)?l.GetString():null);
    }
}
else if (args[0] == "prepare")
{
    if (File.Exists(Path.Combine(results,"test-checkpoint.json"))) throw new InvalidOperationException("A test baseline already exists. Finish cleanup and archive validation output before preparing again.");
    var baseline = await Call("hyperv_vm_info", new {machine}, label:"baseline-vm");
    Check(baseline.GetProperty("vm").GetProperty("State").GetInt32() == 3, "Live preparation starts with an Off VM");
    var vmId = baseline.GetProperty("vm").GetProperty("Id").GetString()!;
    await Call("hyperv_checkpoints", new {machine}, label:"baseline-checkpoints");
    var name = "HyperVControl-validation-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    var snapshot = await Call("hyperv_checkpoint", new {machine,action="create",checkpoint=name}, label:"test-checkpoint");
    Check(snapshot.GetProperty("success").GetBoolean(), "Own baseline checkpoint created");
    await Call("hyperv_power", new {machine,action="start"});
    // Read only the existing credential for the user-authorized VM; never print its contents.
    var credentialFile = Environment.GetEnvironmentVariable("HYPERV_CONTROL_TEST_CREDENTIAL_FILE")
        ?? throw new InvalidOperationException("Set HYPERV_CONTROL_TEST_CREDENTIAL_FILE to a per-VM credential JSON file for this explicit live test.");
    using var entries = JsonDocument.Parse(await File.ReadAllTextAsync(credentialFile));
    var entry = entries.RootElement.EnumerateObject().Single(p => p.Name.Equals(vmId,StringComparison.OrdinalIgnoreCase)).Value;
    Exception? last = null;
    for (var i = 0; i < 12; i++)
    {
        try { await Call("hyperv_credential", new {machine,username=entry.GetProperty("userName").GetString(),password=entry.GetProperty("password").GetString()}); last=null; break; }
        catch(Exception ex) { last=ex; await Task.Delay(5000); }
    }
    if(last!=null) throw last;
    await Call("hyperv_guest_run",new {machine,script="[Security.Principal.WindowsIdentity]::GetCurrent().Name; whoami /groups; Get-CimInstance Win32_OperatingSystem | Select Caption,Version | ConvertTo-Json -Compress"},label:"guest-identity");
}
else if (args[0] == "guest")
{
    var failedExit = await Call("hyperv_guest_run",new {machine,script="[Console]::WriteLine('stdout-中文'); [Console]::Error.WriteLine('stderr-marker'); exit 7"},true,"guest-exit");
    Check(failedExit.GetProperty("exitCode").GetInt32() == 7 && failedExit.GetProperty("stdout").GetString()!.Contains("stdout-中文") && failedExit.GetProperty("stderr").GetString()!.Contains("stderr-marker"), "Guest exit code and separate UTF-8 output streams");
    await Call("hyperv_guest_run",new {machine,script="'guest-success'"},label:"guest-success");
    var source=Path.Combine(results,"roundtrip 空格.txt");
    await File.WriteAllTextAsync(source,"HyperVControl UTF-8 数据 123\n");
    await Call("hyperv_copy",new {machine,direction="to_guest",source,destination=@"C:\ProgramData\HyperVControl\test 空格.txt"},label:"copy-to-guest");
    var dest=Path.Combine(results,"roundtrip-return.txt");
    await Call("hyperv_copy",new {machine,direction="from_guest",source=@"C:\ProgramData\HyperVControl\test 空格.txt",destination=dest},label:"copy-from-guest");
    var sentBytes = await File.ReadAllBytesAsync(source);
    var receivedBytes = await File.ReadAllBytesAsync(dest);
    Check(sentBytes.SequenceEqual(receivedBytes),"Guest file roundtrip byte equality");
    await Call("hyperv_guest_files",new {machine,path=@"C:\ProgramData\HyperVControl\test 空格.txt",action="read"});
    await Call("hyperv_console",new {machine,action="open",session="basic"});
    await Call("hyperv_screenshot",new {machine},label:"basic-before");
    await Call("hyperv_input",new {machine,action="key",text="WIN+R"});
    await Call("hyperv_input",new {machine,action="type",text="notepad"});
    await Call("hyperv_input",new {machine,action="key",text="ENTER"});
    await Task.Delay(1500);
    await Call("hyperv_screenshot",new {machine},label:"basic-after");
}
else if (args[0] == "ui")
{
    await Call("hyperv_guest_setup",new {machine});
    await Call("hyperv_ui",new {machine,command="status"},true,"ui-target-required");
    await Call("hyperv_ui",new {machine,command="list-windows"},label:"ui-windows");
}
else if(args[0] == "final-guest")
{
    var emptyArguments = await Call("hyperv_guest_process",new {machine,executable=@"C:\Windows\System32\hostname.exe"},label:"guest-process-empty-argv");
    Check(emptyArguments.GetProperty("exitCode").GetInt32()==0,"Native guest process with no arguments");
    var nativeExit = await Call("hyperv_guest_process",new {machine,executable=@"C:\Windows\System32\cmd.exe",arguments=new[]{"/d","/c","echo argv-marker & exit /b 23"}},true,"guest-process-exit");
    Check(nativeExit.GetProperty("exitCode").GetInt32()==23 && nativeExit.GetProperty("stdout").GetString()!.Contains("argv-marker"),"Native argv and nonzero exit status");
    var paused=await Call("hyperv_power",new {machine,action="pause"},label:"pause-vm");
    Check(paused.GetProperty("machine").GetProperty("state").GetString()=="paused","Paused VM observed");
    await Call("hyperv_power",new {machine,action="resume"},label:"resume-vm");
    var saved=await Call("hyperv_power",new {machine,action="save"},label:"save-vm");
    Check(saved.GetProperty("machine").GetProperty("state").GetString()=="saved","Saved VM observed");
    await Call("hyperv_power",new {machine,action="start"},label:"resume-saved-vm");
    await Call("hyperv_guest_run",new {machine,script="'restored from saved state'"},label:"saved-state-guest-alive");
}
else if(args[0] is "debug" or "debug-host")
{
    var start=await Call("hyperv_debug_open",new {mode="launch",path=@"C:\Windows\System32\cmd.exe",arguments=new[]{"/c","ping -n 60 127.0.0.1 >nul"},debuggerDirectory=Path.Combine(root,"build","hyperv-control","windbg","amd64")});
    var id=start.GetProperty("sessionId").GetString();
    var commandResult = await Call("hyperv_debug_command",new {sessionId=id,command=".echo HVC_DEBUG_READY; r; k; lm",waitMs=10000});
    Check(!commandResult.GetProperty("pending").GetBoolean(), "Debugger completed command recognized");
    await Call("hyperv_debug_command",new {sessionId=id,command=".echo HVC_SECOND_COMMAND",waitMs=10000},label:"second-debug-command");
    var targetIdentity = await Call("hyperv_debug_command",new {sessionId=id,command="? @$tpid",waitMs=10000},label:"debug-target-id");
    var pidMatch = System.Text.RegularExpressions.Regex.Match(targetIdentity.GetProperty("output").GetString()!, @"Evaluate expression:\s*(\d+)");
    Check(pidMatch.Success,"Target process ID observed");
    var targetPid = int.Parse(pidMatch.Groups[1].Value);
    var running = await Call("hyperv_debug_command",new {sessionId=id,command="g",waitMs=200},label:"debug-continue");
    Check(running.GetProperty("pending").GetBoolean(), "Continue returns a pending command");
    await Call("hyperv_debug_break",new {sessionId=id},label:"debug-interrupt");
    await Task.Delay(500);
    await Call("hyperv_debug_command",new {sessionId=id,command="r",waitMs=10000},label:"debug-registers-after-break");
    await Call("hyperv_debug_close",new {sessionId=id});
    using(var target = System.Diagnostics.Process.GetProcessById(targetPid)) Check(!target.HasExited,"Detach preserves the target process");
    if (args[0] == "debug")
    {
    await Call("hyperv_guest_debug_setup",new {machine,debuggerDirectory=Path.Combine(root,"build","hyperv-control","windbg","amd64")});
    var guest=await Call("hyperv_guest_debug_open",new {machine,mode="launch",path=@"C:\Windows\System32\cmd.exe",arguments=new[]{"/c","ping -n 60 127.0.0.1 >nul"},guestDebuggerDirectory=@"C:\ProgramData\HyperVControl\debug\engines\amd64"});
    var worker=guest.GetProperty("workerId").GetString();
    var guestCommand = await Call("hyperv_guest_debug",new {machine,workerId=worker,action="command",command=".echo HVC_GUEST_DEBUG_READY; r; k; lm",waitMs=10000},label:"guest-debug-command");
    Check(!guestCommand.GetProperty("pending").GetBoolean(),"Guest debugger command completion");
    await Call("hyperv_guest_debug",new {machine,workerId=worker,action="close"},label:"guest-debug-close");
    }
}
else if(args[0] is "kernel-serial" or "kernel-net" or "kernel-recover")
{
    string connect;
    if(args[0] == "kernel-net")
    {
        var configured=await Call("hyperv_kernel_configure",new {machine,action="kdnet",hostIp=args[2],port=50009},label:"kernel-net-configured");
        connect=configured.GetProperty("kernelAttachString").GetString()!;
    }
    else connect=@"com:pipe,port=\\.\pipe\HyperVControl-validation,resets=0,reconnect";
    var kd=await Call("hyperv_debug_open",new {mode="kernel",connectString=connect,debuggerDirectory=Path.Combine(root,"build","hyperv-control","windbg","amd64"),symbolPath=Path.Combine(root,"build","hyperv-control","symbols")},label:args[0]+"-open");
    var session=kd.GetProperty("sessionId").GetString();
    if(args[0] != "kernel-recover") await Call("hyperv_power",new {machine,action="restart"},label:args[0]+"-reboot");
    await Task.Delay(12000);
    await Call("hyperv_debug_break",new {sessionId=session},label:args[0]+"-break");
    var output=await Call("hyperv_debug_command",new {sessionId=session,command=".echo HVC_KERNEL_READY; vertarget; r; k",waitMs=10000},label:args[0]+"-commands");
    for(var retry=0; output.GetProperty("pending").GetBoolean() && retry < 12; retry++)
    {
        await Task.Delay(10000);
        output=await Call("hyperv_debug_poll",new {sessionId=session},label:args[0]+"-poll");
    }
    Check(!output.GetProperty("pending").GetBoolean() && output.GetProperty("output").GetString()!.Contains("HVC_KERNEL_READY"),"Kernel debugger command completed");
    await Call("hyperv_debug_close",new {sessionId=session},label:args[0]+"-close");
    await Call("hyperv_guest_run",new {machine,script="'Guest resumed after kernel detach'"},label:args[0]+"-guest-alive");
}
else if(args[0] == "cleanup")
{
    var checkpoint=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(results,"test-checkpoint.json"))).RootElement.GetProperty("checkpoint").GetProperty("id").GetString();
    await Call("hyperv_console",new {machine,action="close"});
    await Call("hyperv_power",new {machine,action="turn_off"});
    await Call("hyperv_checkpoint",new {machine,action="restore",checkpoint},label:"restore-test-baseline");
    await Call("hyperv_checkpoint",new {machine,action="remove",checkpoint},label:"remove-test-baseline");
    var finalVm = await Call("hyperv_vm_info",new {machine},label:"final-vm");
    Check(finalVm.GetProperty("vm").GetProperty("State").GetInt32()==3,"Initial Off state restored");
    var finalCheckpoints=await Call("hyperv_checkpoints",new {machine},label:"final-checkpoints");
    var original=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(results,"baseline-checkpoints.json"))).RootElement;
    var originalIds=original.GetProperty("checkpoints").EnumerateArray().Select(c=>c.GetProperty("id").GetString()).ToHashSet();
    var finalIds=finalCheckpoints.GetProperty("checkpoints").EnumerateArray().Select(c=>c.GetProperty("id").GetString()).ToHashSet();
    Check(originalIds.SetEquals(finalIds),"All original checkpoint IDs preserved; only test checkpoint removed");
    Check(original.GetProperty("currentParentCheckpointId").GetString()==finalCheckpoints.GetProperty("currentParentCheckpointId").GetString(),"Original checkpoint branch restored");
}
else throw new ArgumentException("Unknown validation phase.");
Console.WriteLine(args[0].ToUpperInvariant()+" PASS");
