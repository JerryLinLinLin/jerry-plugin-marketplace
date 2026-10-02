using System.Text.Json;
using HyperVControl.Extracted;

namespace HyperVControl.Services;

internal sealed class GuestService(PowerShellRunner runner, CredentialStore credentials)
{
    internal static string Session(Guid id, GuestCredential c) => $$"""
        $guestUser = {{PowerShellRunner.DecodeUtf8(c.UserName)}}
        $guestPassword = ConvertTo-SecureString ({{PowerShellRunner.DecodeUtf8(c.Password)}}) -AsPlainText -Force
        $credential = [pscredential]::new($guestUser, $guestPassword)
        $session = New-PSSession -VMId ([guid]'{{id:D}}') -Credential $credential -ErrorAction Stop
        """ + "\n";

    public async Task<HyperVGuestCommandResult> RunTextAsync(Guid id, string script, string profile, CancellationToken ct)
    {
        try
        {
            var result = await InvokeAsync(id, script, credentials.Resolve(id, profile), ct, 300);
            return new(true, result.GetProperty("output").GetString(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(false, null, ex.Message); }
    }

    internal Task<JsonElement> InvokeAsync(Guid id, string script, GuestCredential c, CancellationToken ct, int timeout = 120) =>
        runner.JsonAsync(Session(id, c) + $$"""
            try {
                $text = Invoke-Command -Session $session -ErrorAction Stop -ScriptBlock {
                    param($encoded)
                    $ErrorActionPreference='Stop'
                    & ([scriptblock]::Create([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($encoded)))) | Out-String -Width 32768
                } -ArgumentList '{{PowerShellRunner.Encoded(script)}}'
                @{ output=[string]$text } | ConvertTo-Json -Compress -Depth 8
            } finally { Remove-PSSession -Session $session -ErrorAction SilentlyContinue }
            """, ct, timeout);

    public async Task<JsonElement> RunAsync(Guid id, string script, string profile, int timeoutSeconds, CancellationToken ct)
    {
        var inner = BuildRunScript(script, timeoutSeconds);
        var result = await InvokeAsync(id, inner, credentials.Resolve(id, profile), ct, timeoutSeconds + 45);
        return JsonDocument.Parse(result.GetProperty("output").GetString()!).RootElement.Clone();
    }

    // Kept separate from transport so the exact guest process logic can be exercised
    // under Windows PowerShell without mutating a VM.
    internal static string BuildRunScript(string script, int timeoutSeconds)
    {
        if (timeoutSeconds is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Use 1-300 seconds.");
        if (script.Length > 200000) throw new ArgumentException("Guest script exceeds 200,000 characters.");
        var childScript = "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'\n" + script;
        return $$"""
            $p=$null
            try {
                # Windows PowerShell's .NET Framework has no StandardInputEncoding
                # property; redirected input inherits Console.InputEncoding instead.
                [Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
                $psi=[Diagnostics.ProcessStartInfo]::new("$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe")
                # Only a fixed bootstrap goes on the command line. Script length never
                # contributes to Windows' CreateProcess command-line limit.
                $psi.Arguments='-NoLogo -NoProfile -NonInteractive -OutputFormat Text -EncodedCommand {{PowerShellRunner.Encoded(PowerShellRunner.StdinBootstrap)}}'
                $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
                $psi.RedirectStandardInput=$true
                $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
                $psi.StandardOutputEncoding=[Text.Encoding]::UTF8; $psi.StandardErrorEncoding=[Text.Encoding]::UTF8
                $p=[Diagnostics.Process]::Start($psi)
                $outTask=$p.StandardOutput.ReadToEndAsync(); $errTask=$p.StandardError.ReadToEndAsync()
                try { $p.StandardInput.Write({{PowerShellRunner.DecodeUtf8(childScript)}}) }
                finally { $p.StandardInput.Close() }
                $done=$p.WaitForExit({{timeoutSeconds * 1000}})
                if (-not $done) { & taskkill.exe /PID $p.Id /T /F 2>&1 | Out-Null; $p.WaitForExit(5000) | Out-Null }
                $out=[string]$outTask.Result
                $err=[string]$errTask.Result
                $truncated=$out.Length -gt 400000 -or $err.Length -gt 100000
                if ($out.Length -gt 400000) { $out=$out.Substring(0,400000) }
                if ($err.Length -gt 100000) { $err=$err.Substring(0,100000) }
                @{ok=($done -and $p.ExitCode -eq 0); exitCode= $(if($done){$p.ExitCode}else{-1}); timedOut=(-not $done); stdout=$out; stderr=$err; truncated=$truncated} | ConvertTo-Json -Compress
            } finally { if ($p) { $p.Dispose() } }
            """;
    }

    public async Task<JsonElement> CopyAsync(Guid id, string source, string destination, bool fromGuest, string profile, CancellationToken ct)
    {
        ControlService.FullPath(source); ControlService.FullPath(destination);
        if (fromGuest)
        {
            var full = Path.GetFullPath(destination);
            foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var blocked = Environment.GetFolderPath(folder);
                if (blocked.Length > 0 && (full.Equals(blocked, StringComparison.OrdinalIgnoreCase) || full.StartsWith(blocked.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Choose a host destination outside Windows and Program Files.");
            }
        }
        var c = credentials.Resolve(id, profile);
        var script = HyperVGuestUiService.BuildHostFileCopyScript(id, c.UserName, c.Password, source, destination, fromGuest);
        var result = await runner.JsonAsync(script, ct, 300);
        if (!result.GetProperty("success").GetBoolean()) throw new InvalidOperationException(result.GetProperty("error").GetString());
        return JsonSerializer.SerializeToElement(new { success = true, bytesCopied = long.Parse(result.GetProperty("exePath").GetString()!), source, destination });
    }
}
