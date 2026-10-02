using System.Text.Json;
using HyperVControl.Extracted;
using HyperVControl.Services;
using ModelContextProtocol.Protocol;

namespace HyperVControl.Tests;

internal static class ReviewRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var runner = new PowerShellRunner();
        string[] values = [
            "", "O'Brien", "C:\\VMs\\Smart\u2019Name.vhdx", "\u2018\u2019\u201a\u201b",
            "literal $([Console]::WriteLine('unexpected')) ` text\r\nnext line",
            .. new[] { '\'', '\u2018', '\u2019', '\u201a', '\u201b' }
                .Select(quote => $"VM{quote}; $injected = $true; #")
        ];
        (string Name, Func<string, string> Quote)[] helpers = [
            ("Host", PowerShellRunner.Literal),
            ("Guest UI", HyperVGuestUiService.QuotePowerShellLiteral)
        ];
        foreach (var (name, quote) in helpers)
        {
            var script = "$injected=$false\n$values=@(\n" + string.Join('\n', values.Select(quote))
                + "\n)\n@{values=$values; injected=$injected} | ConvertTo-Json -Compress";
            var result = await runner.JsonAsync(script, default);
            check(!result.GetProperty("injected").GetBoolean()
                && result.GetProperty("values").EnumerateArray().Select(v => v.GetString()).SequenceEqual(values),
                name + " literals preserve ASCII/Unicode quotes without executing embedded commands");
        }

        // Execute the exact generated guest wrapper locally under Windows PowerShell.
        // These regressions need process creation and pipes, not Hyper-V or elevation.
        foreach (var length in new[] { 13_001, 200_000 })
        {
            const string suffix = "\n[Console]::Out.Write('tail-\u03a9'); [Console]::Error.Write('stderr-\u03a9'); exit 7";
            var script = "#" + new string('\u2603', length - suffix.Length - 1) + suffix;
            var result = await runner.JsonAsync(GuestService.BuildRunScript(script, 30), default, 45);
            check(result.GetProperty("exitCode").GetInt32() == 7
                && !result.GetProperty("ok").GetBoolean()
                && !result.GetProperty("timedOut").GetBoolean()
                && result.GetProperty("stdout").GetString() == "tail-\u03a9"
                && result.GetProperty("stderr").GetString() == "stderr-\u03a9",
                $"{length:N0}-character guest script reaches its tail with separate UTF-8 streams and exit code");
        }
        var empty = await runner.JsonAsync(GuestService.BuildRunScript("", 30), default, 45);
        check(empty.GetProperty("ok").GetBoolean() && empty.GetProperty("stdout").GetString() == "",
            "Empty guest script completes after stdin EOF");
        var timedOut = await runner.JsonAsync(GuestService.BuildRunScript("Start-Sleep -Seconds 30", 1), default, 15);
        check(timedOut.GetProperty("timedOut").GetBoolean() && timedOut.GetProperty("exitCode").GetInt32() == -1,
            "Guest child timeout still terminates execution with stdin transport");
        try
        {
            GuestService.BuildRunScript(new string('x', 200_001), 30);
            throw new Exception("Oversized guest script was accepted.");
        }
        catch (ArgumentException) { check(true, "Guest script length guard rejects over-limit input before execution"); }

        (string Name, string Json, bool Error)[] startupCases = [
            ("startup failure", "{\"success\":false,\"sessionId\":\"failed\",\"result\":{\"exitCode\":1}}", true),
            ("exception envelope", "{\"ok\":false,\"error\":\"Debugger unavailable\"}", true),
            ("success=false with ok=true", "{\"success\":false,\"ok\":true}", true),
            ("ok=false with success=true", "{\"success\":true,\"ok\":false}", true),
            ("successful startup", "{\"success\":true,\"sessionId\":\"running\"}", false)
        ];
        foreach (var (name, json, error) in startupCases)
        {
            using var state = JsonDocument.Parse(json);
            var result = HyperVTools.Result(new HyperVTools.GuestDebugStartup("worker-id", "task-name", state.RootElement));
            using var content = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
            check(result.IsError == error
                && content.RootElement.GetProperty("success").GetBoolean() == !error
                && content.RootElement.GetProperty("workerId").GetString() == "worker-id"
                && content.RootElement.GetProperty("taskName").GetString() == "task-name"
                && JsonElement.DeepEquals(content.RootElement.GetProperty("result"), state.RootElement),
                $"Guest debugger {name} preserves diagnostics and produces the correct MCP error flag");
        }
    }
}
