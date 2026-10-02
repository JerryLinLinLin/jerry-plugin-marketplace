using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HyperVControl.Services;

internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

internal static class ControlPaths
{
    internal static string DataRoot => Environment.GetEnvironmentVariable("HYPERV_CONTROL_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperVControl");
}

internal sealed class PowerShellRunner
{
    internal static string Literal(string value)
    {
        // Match PowerShell's IsSingleQuote, including its four Unicode delimiters.
        // Doubling the original character preserves the value instead of normalizing it.
        // https://github.com/PowerShell/PowerShell/blob/master/src/System.Management.Automation/engine/parser/CharTraits.cs
        var quoted = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var character in value)
        {
            if (character is '\'' or '\u2018' or '\u2019' or '\u201a' or '\u201b')
                quoted.Append(character);
            quoted.Append(character);
        }
        return quoted.Append('\'').ToString();
    }

    internal const string StdinBootstrap =
        "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false); $s=[Console]::In.ReadToEnd(); & ([scriptblock]::Create($s))";
    internal static string Encoded(string value) => Convert.ToBase64String(Encoding.Unicode.GetBytes(value));
    internal static string DecodeUtf8(string value) =>
        "[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "'))";

    public async Task<ProcessResult> RunAsync(string script, CancellationToken ct, int timeoutSeconds = 120)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", StdinBootstrap })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start Windows PowerShell.");
        // PowerShell scripts and guest credentials travel on stdin, never its process command line.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(("[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)\n" + script).AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"PowerShell operation exceeded {timeoutSeconds}s. Inspect the VM before retrying; a completed guest mutation is not rolled back.");
        }
    }

    public async Task<JsonElement> JsonAsync(string script, CancellationToken ct, int timeoutSeconds = 120)
    {
        var result = await RunAsync("""
            $ErrorActionPreference='Stop'
            $ProgressPreference='SilentlyContinue'
            $WarningPreference='SilentlyContinue'
            try {
            """ + "\n" + script + "\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }", ct, timeoutSeconds);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Stderr.Trim());
        try { return JsonDocument.Parse(result.Stdout).RootElement.Clone(); }
        catch (JsonException ex) { throw new InvalidOperationException("PowerShell returned an invalid JSON result: " + result.Stdout[..Math.Min(2000, result.Stdout.Length)], ex); }
    }
}
