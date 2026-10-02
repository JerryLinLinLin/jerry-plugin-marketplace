// Extracted and adapted from RaivenX b5971dda1fa2050dde36b8b22fda9726df92cb48. See THIRD-PARTY-NOTICES.md.
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyperVControl.Services;

namespace HyperVControl.Extracted;

/// <summary>
/// Installs and runs Microsoft's winapp CLI inside a Windows guest for semantic UI automation.
/// PowerShell Direct processes can never reach the interactive desktop, so every
/// <c>winapp ui</c> invocation is bridged through a reusable scheduled task whose principal is
/// the guest's signed-in user with an Interactive logon. The host downloads the release once,
/// verifies its published SHA-256 digest, and copies it in over PowerShell Direct, so the guest
/// itself never needs internet access.
/// </summary>
public sealed class HyperVGuestUiService
{
    /// <summary>Stable install root inside the guest.</summary>
    internal const string GuestInstallRoot = @"C:\ProgramData\HyperVControl\winapp";

    /// <summary>Reusable scheduled task that runs winapp in the interactive session.</summary>
    internal const string GuestTaskName = "HyperVControl-guest-ui";

    internal const string EnvelopeBeginMarker = "HYPERVCONTROL-GUEST-UI-BEGIN";
    internal const string EnvelopeEndMarker = "HYPERVCONTROL-GUEST-UI-END";

    /// <summary>Exact GitHub release asset installed into the guest.</summary>
    internal const string ReleaseAssetName = "winappcli-x64.zip";

    /// <summary>
    /// The privileged protocol rejects guest scripts above 32,768 characters, so composed
    /// scripts must stay comfortably below that limit.
    /// </summary>
    internal const int MaxGuestScriptChars = 30_000;

    internal const int MaxUiArguments = 32;
    internal const int MaxUiArgumentLength = 1_024;
    internal const int MaxTotalUiArgumentLength = 6_000;

    internal const string NotInstalledError =
        "winapp is not installed in this guest. Run the hyperv_guest_setup command first.";

    internal const string NoInteractiveSessionError =
        "No user is signed in to the guest's interactive desktop. Sign in to the VM console or Enhanced session, then retry.";

    internal const string DesktopLockedError =
        "The guest desktop is locked. Unlock the guest session, then retry.";

    internal const string GuestUnresponsiveError =
        "The guest did not return the winapp result in time. The virtual machine may be busy; retry.";

    internal const string UnreadableResultError =
        "The guest returned an unreadable winapp result.";

    internal static string TimeoutErrorText(int timeoutSeconds) =>
        $"winapp did not finish within {timeoutSeconds} seconds in the guest's interactive session.";

    /// <summary>
    /// The winapp ui subcommands that may be composed through this service. Keyboard input and
    /// image capture are deliberately absent: the console channel already does both natively and
    /// more cheaply (see <see cref="RedirectedUiCommands"/>), so this family stays focused on what
    /// only UI Automation can do — finding elements and driving them through their patterns.
    /// </summary>
    internal static readonly string[] UiCommands =
    [
        "status",
        "inspect",
        "search",
        "list-windows",
        "invoke",
        "click",
        "focus",
        "get-value",
        "set-value",
        "get-property",
        "wait-for",
        "scroll",
    ];

    /// <summary>
    /// winapp subcommands this service refuses on purpose, each answered with the native tool to
    /// use instead. A model that reaches for them gets the redirect at the point of failure
    /// rather than a bare "unknown command".
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> RedirectedUiCommands =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["send-keys"] =
                "The hyperv_ui command does not send keyboard input. Put text in a field with "
                + "its \"set-value\" subcommand (fastest, no focus needed), or focus the target "
                + "with \"focus\" and then use the hyperv_input command with action=type for "
                + "text or action=key for a chord.",
            ["screenshot"] =
                "The hyperv_ui command does not capture images. Use the hyperv_screenshot "
                + "command, which captures the whole console to a PNG on the host and returns "
                + "its path and dimensions.",
        };

    private static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/microsoft/winappCli/releases/latest");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    /// <summary>One gate for the shared per-user archive cache across all panel instances.</summary>
    private static readonly SemaphoreSlim ArchiveGate = new(1, 1);

    /// <summary>
    /// Resolves the installed winapp executable. The zip layout is not contractual, so the
    /// stable root path is preferred and a recursive search is the fallback. This snippet is
    /// the single source reused by the probe, the task action, and the install script.
    /// </summary>
    private static readonly string ResolveWinappSnippet =
        $$"""
        $installRoot = '{{GuestInstallRoot}}'
        $winappExe = Join-Path $installRoot 'winapp.exe'
        if (-not (Test-Path -LiteralPath $winappExe)) {
            $winappExe = Get-ChildItem -LiteralPath $installRoot -Recurse -File -Filter 'winapp.exe' -ErrorAction SilentlyContinue |
                Select-Object -First 1 -ExpandProperty FullName
        }
        if ([string]::IsNullOrWhiteSpace([string]$winappExe)) {
            $winappExe = $null
        }
        """;

    /// <summary>
    /// Shared prologue for scripts that run inside the guest through PowerShell Direct. The
    /// result is emitted as base64 in short fixed-width lines because the broker renders remote
    /// output through <c>Out-String -Width 4096</c>, which would wrap one long JSON line.
    /// </summary>
    private static readonly string GuestScriptPrologue =
        $$"""
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $WarningPreference = 'SilentlyContinue'
            $env:WINAPP_CLI_TELEMETRY_OPTOUT = '1'

        function Send-HyperVControlGuestUiResult([hashtable]$Payload) {
            $json = $Payload | ConvertTo-Json -Compress
            $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
            Write-Output '{{EnvelopeBeginMarker}}'
            for ($index = 0; $index -lt $encoded.Length; $index += 3000) {
                Write-Output $encoded.Substring($index, [Math]::Min(3000, $encoded.Length - $index))
            }
            Write-Output '{{EnvelopeEndMarker}}'
        }

        function Resolve-HyperVControlWinapp {
            {{ResolveWinappSnippet}}
            return $winappExe
        }
        """;

    private readonly Func<Guid, string, CancellationToken, Task<HyperVGuestCommandResult>>
        _runGuestScript;
    private readonly Func<Guid, string, CancellationToken, Task<HyperVGuestCommandResult>>
        _installArchive;
    private readonly HttpClient _httpClient;
    private readonly string _cacheRoot;

    public HyperVGuestUiService(
        Func<Guid, string, CancellationToken, Task<HyperVGuestCommandResult>> runGuestScript,
        Func<Guid, string, CancellationToken, Task<HyperVGuestCommandResult>> installArchive)
        : this(
            runGuestScript,
            installArchive,
            SharedHttpClient,
            Path.Combine(ControlPaths.DataRoot, "tools", "winapp"))
    {
    }

    internal HyperVGuestUiService(
        Func<Guid, string, CancellationToken, Task<HyperVGuestCommandResult>> runGuestScript,
        Func<Guid, string, CancellationToken, Task<HyperVGuestCommandResult>> installArchive,
        HttpClient httpClient,
        string cacheRoot)
    {
        _runGuestScript = runGuestScript
            ?? throw new ArgumentNullException(nameof(runGuestScript));
        _installArchive = installArchive
            ?? throw new ArgumentNullException(nameof(installArchive));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _cacheRoot = Path.GetFullPath(cacheRoot);
    }

    /// <summary>
    /// Ensures winapp is installed inside the guest. The probe runs through the normal guest
    /// command path; when winapp is missing, the verified release archive is copied in from the
    /// host over PowerShell Direct and extracted at <see cref="GuestInstallRoot"/>.
    /// </summary>
    public async Task<HyperVGuestUiSetupResult> EnsureWinappAsync(
        Guid machineId,
        CancellationToken ct = default)
    {
        ValidateMachineId(machineId);

        var probe = await ProbeAsync(machineId, ct).ConfigureAwait(false);
        if (probe.Error is not null)
        {
            return new(false, false, null, Downloaded: false, probe.Error);
        }

        if (probe.Installed)
        {
            return new(true, true, probe.Version, Downloaded: false, null);
        }

        string archivePath;
        bool downloaded;
        try
        {
            (archivePath, downloaded) = await EnsureHostArchiveAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is HttpRequestException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or JsonException)
        {
            return new(
                false,
                false,
                null,
                Downloaded: false,
                FirstLine(ex.Message, "The winapp release could not be downloaded.")
                + " Check the host's internet access, then retry.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The download's own timeout, not the caller stopping the turn.
            return new(
                false,
                false,
                null,
                Downloaded: false,
                "Downloading the winapp tool from GitHub timed out. Check the host's internet "
                + "access, then retry.");
        }

        // The copy needs a PowerShell Direct session, which requires Hyper-V administrator
        // rights the app process does not hold — it runs in the elevated broker like every
        // other guest operation, with the broker's usual credential cache and prompt.
        var copyResult = await _installArchive(machineId, archivePath, ct).ConfigureAwait(false);
        if (!copyResult.Success)
        {
            return new(
                false,
                false,
                null,
                downloaded,
                FirstLine(copyResult.Error, "winapp could not be copied into the guest."));
        }

        var verify = await ProbeAsync(machineId, ct).ConfigureAwait(false);
        if (verify.Error is not null)
        {
            return new(false, false, null, downloaded, verify.Error);
        }

        if (!verify.Installed)
        {
            return new(
                false,
                false,
                null,
                downloaded,
                "winapp was copied into the guest but winapp.exe could not be found afterwards.");
        }

        return new(true, true, verify.Version, downloaded, null);
    }

    /// <summary>
    /// Runs one <c>winapp ui</c> command in the guest's signed-in interactive session and
    /// returns its output and exit code. Only allow-listed ui subcommands are composable, so
    /// no other guest executable or shell can be reached through this path.
    /// </summary>
    public async Task<HyperVGuestUiRunResult> RunGuestUiAsync(
        Guid machineId,
        string uiCommand,
        IReadOnlyList<string> uiArguments,
        TimeSpan timeout,
        bool elevated = false,
        CancellationToken ct = default)
    {
        ValidateMachineId(machineId);
        ArgumentNullException.ThrowIfNull(uiArguments);
        var command = ValidateUiCommand(uiCommand);
        ValidateUiArguments(uiArguments);
        var timeoutSeconds = ValidateTimeout(timeout);

        var script = BuildRunUiScript(
            command,
            uiArguments,
            timeoutSeconds,
            Guid.NewGuid().ToString("N"),
            elevated);

        HyperVGuestCommandResult result;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 60));
        try
        {
            result = await _runGuestScript(machineId, script, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, null, null, null, GuestUnresponsiveError);
        }

        if (!result.Success)
        {
            return new(
                false,
                null,
                null,
                null,
                FirstLine(result.Error, "PowerShell Direct could not run winapp in the guest."));
        }

        var envelope = ParseGuestEnvelope(result.Output);
        if (envelope is null)
        {
            return new(false, null, null, null, UnreadableResultError);
        }

        return envelope.Status switch
        {
            "ok" => new(
                envelope.ExitCode == 0,
                envelope.ExitCode ?? -1,
                envelope.Stdout,
                envelope.Stderr,
                envelope.ExitCode == 0 ? null : FirstLine(envelope.Stderr, "winapp exited with a nonzero code.")),
            "notInstalled" => new(false, null, null, null, NotInstalledError),
            "noSession" => new(false, null, null, null, NoInteractiveSessionError),
            "locked" => new(false, null, null, null, DesktopLockedError),
            "timeout" => new(false, null, null, null, TimeoutErrorText(timeoutSeconds)),
            _ => new(
                false,
                null,
                null,
                null,
                FirstLine(envelope.Error, "winapp failed inside the guest.")),
        };
    }

    /// <summary>Guest-side probe: reports whether winapp.exe exists and its version.</summary>
    internal static string BuildProbeScript() =>
        GuestScriptPrologue
        + """

          try {
              $winappExe = Resolve-HyperVControlWinapp
              if ($null -eq $winappExe) {
                  Send-HyperVControlGuestUiResult @{ status = 'ok'; installed = $false; version = $null }
              } else {
                  $version = $null
                  try {
                      $previousPreference = $ErrorActionPreference
                      $ErrorActionPreference = 'Continue'
                      $version = [string](& $winappExe --version 2>&1 | Where-Object { $_ -match '^\d+\.\d+\.\d+' } | Select-Object -Last 1)
                      $ErrorActionPreference = $previousPreference
                  } catch {
                      $version = $null
                  }
                  Send-HyperVControlGuestUiResult @{ status = 'ok'; installed = $true; version = $version }
              }
          } catch {
              Send-HyperVControlGuestUiResult @{ status = 'error'; error = $_.Exception.Message }
          }
          """;

    /// <summary>
    /// Guest-side bridge into the interactive desktop: resolves the signed-in user, refuses a
    /// locked or empty desktop with distinct statuses, then registers and starts the reusable
    /// <see cref="GuestTaskName"/> scheduled task whose action runs winapp with its output
    /// redirected to unique job files. The exit-code file is written last, so its existence is
    /// the completion sentinel the poll loop waits for.
    /// </summary>
    internal static string BuildRunUiScript(
        string uiCommand,
        IReadOnlyList<string> uiArguments,
        int timeoutSeconds,
        string jobId,
        bool elevated = false)
    {
        var encodedTaskCommand = EncodePowerShellCommand(
            BuildTaskScript(uiCommand, uiArguments, jobId));
        // A task registered with Highest gets the signed-in admin's full token with no UAC
        // consent, which is the only way to read or drive a guest app that auto-elevates
        // (Task Manager). It is opt-in because the reverse also bites: UIPI blocks synthetic
        // input from a HIGH-integrity process into an AppContainer target, so an always-
        // elevated bridge would break Start, Search, and Settings.
        var runLevel = elevated ? " -RunLevel Highest" : string.Empty;
        return GuestScriptPrologue
            + $$"""

              try {
                  $winappExe = Resolve-HyperVControlWinapp
                  $interactiveUser = $null
                  $interactiveSessionId = $null
                  $explorer = Get-CimInstance -ClassName Win32_Process -Filter "Name = 'explorer.exe'" -ErrorAction SilentlyContinue |
                      Select-Object -First 1
                  if ($null -ne $explorer) {
                      $interactiveSessionId = [int]$explorer.SessionId
                      $owner = Invoke-CimMethod -InputObject $explorer -MethodName GetOwner -ErrorAction SilentlyContinue
                      if ($null -ne $owner -and $owner.ReturnValue -eq 0 -and -not [string]::IsNullOrWhiteSpace($owner.User)) {
                          if ([string]::IsNullOrWhiteSpace($owner.Domain)) {
                              $interactiveUser = $owner.User
                          } else {
                              $interactiveUser = $owner.Domain + '\' + $owner.User
                          }
                      }
                  }
                  if ($null -eq $interactiveUser) {
                      $interactiveUser = (Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction SilentlyContinue).UserName
                  }

                  # The lock check must stay inside the interactive user's own session: under an
                  # Enhanced Session the desktop is an RDP session while the CONSOLE session sits
                  # at its lock screen running LogonUI, so a machine-wide LogonUI check reports
                  # "locked" forever. No known session means no lock verdict — winapp itself
                  # fast-fails input verbs on a desktop it cannot reach.
                  $sessionLocked = $false
                  if ($null -ne $interactiveSessionId) {
                      $sessionLocked = $null -ne (Get-Process -Name 'LogonUI' -ErrorAction SilentlyContinue |
                          Where-Object { $_.SessionId -eq $interactiveSessionId })
                  }

                  if ($null -eq $winappExe) {
                      Send-HyperVControlGuestUiResult @{ status = 'notInstalled' }
                  } elseif ([string]::IsNullOrWhiteSpace($interactiveUser)) {
                      Send-HyperVControlGuestUiResult @{ status = 'noSession' }
                  } elseif ($sessionLocked) {
                      Send-HyperVControlGuestUiResult @{ status = 'locked' }
                  } else {
                      $jobsRoot = '{{GuestInstallRoot}}\jobs'
                      New-Item -ItemType Directory -Force -Path $jobsRoot | Out-Null
                      Get-ChildItem -LiteralPath $jobsRoot -File -ErrorAction SilentlyContinue |
                          Where-Object { $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddHours(-6) } |
                          Remove-Item -Force -ErrorAction SilentlyContinue
                      $outFile = Join-Path $jobsRoot '{{jobId}}.out'
                      $errFile = Join-Path $jobsRoot '{{jobId}}.err'
                      $codeFile = Join-Path $jobsRoot '{{jobId}}.code'
                      # powershell.exe is a console program, so Windows creates its console
                      # window before -WindowStyle Hidden can take effect: every call flashed a
                      # window that also STOLE FOREGROUND, which is what makes winapp refuse
                      # input with 'foreground_not_target'. conhost --headless attaches a
                      # pseudoconsole with no window at all, so nothing ever appears or takes
                      # focus. The working directory is the jobs folder because winapp writes
                      # relative output paths there — under the default C:\Windows\system32 an
                      # unelevated write is denied outright.
                      $action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument '--headless powershell.exe -NoProfile -NonInteractive -EncodedCommand {{encodedTaskCommand}}' -WorkingDirectory $jobsRoot
                      $principal = New-ScheduledTaskPrincipal -UserId $interactiveUser -LogonType Interactive{{runLevel}}
                      Stop-ScheduledTask -TaskName '{{GuestTaskName}}' -ErrorAction SilentlyContinue
                      Register-ScheduledTask -TaskName '{{GuestTaskName}}' -Action $action -Principal $principal -Force | Out-Null
                      Start-ScheduledTask -TaskName '{{GuestTaskName}}' -ErrorAction Stop
                      $deadline = [DateTime]::UtcNow.AddSeconds({{timeoutSeconds}})
                      while (-not (Test-Path -LiteralPath $codeFile) -and [DateTime]::UtcNow -lt $deadline) {
                          Start-Sleep -Milliseconds 250
                      }
                      if (-not (Test-Path -LiteralPath $codeFile)) {
                          Stop-ScheduledTask -TaskName '{{GuestTaskName}}' -ErrorAction SilentlyContinue
                          Remove-Item -LiteralPath $outFile, $errFile -Force -ErrorAction SilentlyContinue
                          Send-HyperVControlGuestUiResult @{ status = 'timeout' }
                      } else {
                          # Get-Content -Raw on an EMPTY file returns $null, and [string] of a
                          # null pipeline result is $null (not ''), so read then coalesce. The
                          # 2> redirect always creates a 0-byte .err file on a clean run, which
                          # made every successful winapp call crash on $stderr.TrimEnd().
                          $exitCodeText = [string](Get-Content -LiteralPath $codeFile -Raw)
                          if ($null -eq $exitCodeText) { $exitCodeText = '' }
                          $exitCode = 0
                          if (-not [int]::TryParse($exitCodeText.Trim(), [ref]$exitCode)) {
                              $exitCode = -1
                          }
                          $stdout = ''
                          if (Test-Path -LiteralPath $outFile) {
                              $rawOut = [string](Get-Content -LiteralPath $outFile -Raw -Encoding UTF8)
                              if ($null -ne $rawOut) { $stdout = $rawOut }
                          }
                          $stderr = ''
                          if (Test-Path -LiteralPath $errFile) {
                              $rawErr = [string](Get-Content -LiteralPath $errFile -Raw -Encoding UTF8)
                              if ($null -ne $rawErr) { $stderr = $rawErr }
                          }
                          if ($stdout.Length -gt 400000) {
                              $stdout = $stdout.Substring(0, 400000) + '[truncated by HyperVControl]'
                          }
                          if ($stderr.Length -gt 100000) {
                              $stderr = $stderr.Substring(0, 100000) + '[truncated by HyperVControl]'
                          }
                          Remove-Item -LiteralPath $outFile, $errFile, $codeFile -Force -ErrorAction SilentlyContinue
                          Send-HyperVControlGuestUiResult @{ status = 'ok'; exitCode = $exitCode; stdout = $stdout.TrimEnd(); stderr = $stderr.TrimEnd() }
                      }
                  }
              } catch {
                  $failedAt = ''
                  if ($null -ne $_.InvocationInfo -and $_.InvocationInfo.ScriptLineNumber -gt 0) {
                      $failedAt = ' (guest script line ' + $_.InvocationInfo.ScriptLineNumber + ')'
                  }
                  Send-HyperVControlGuestUiResult @{ status = 'error'; error = ($_.Exception.Message + $failedAt) }
              }
              """;
    }

    /// <summary>
    /// The scheduled task action, encoded for <c>powershell.exe -EncodedCommand</c>. Every
    /// model-provided token is embedded as a single-quoted PowerShell literal, so arguments can
    /// never break out of the fixed <c>winapp ui</c> invocation.
    /// </summary>
    internal static string BuildTaskScript(
        string uiCommand,
        IReadOnlyList<string> uiArguments,
        string jobId)
    {
        var arguments = new List<string>(uiArguments.Count + 2) { "ui", uiCommand };
        arguments.AddRange(uiArguments);
        var argumentLine = QuotePowerShellLiteral(BuildWindowsArgumentLine(arguments));

        // winapp is launched through Start-Process rather than the call operator with a
        // PowerShell redirect: PowerShell turns a native command's stderr into ErrorRecords and
        // renders them with NativeCommandError decoration AND console-width word wrapping, which
        // split winapp's JSON error envelope mid-string and left it unparseable. Start-Process
        // writes both streams as raw bytes, and its working directory is where winapp puts
        // relative output such as PNGs — under the task's default C:\Windows\system32 those
        // writes are denied outright. Guest scripts pay for every character over the wire, so
        // the reasoning lives here and the script itself carries only a one-line reminder.
        return $$"""
            $ErrorActionPreference = 'Continue'
            $env:WINAPP_CLI_TELEMETRY_OPTOUT = '1'
            {{ResolveWinappSnippet}}
            $jobsRoot = '{{GuestInstallRoot}}\jobs'
            $outFile = Join-Path $jobsRoot '{{jobId}}.out'
            $errFile = Join-Path $jobsRoot '{{jobId}}.err'
            $codeFile = Join-Path $jobsRoot '{{jobId}}.code'
            $exitCode = -1
            try {
                if ($null -eq $winappExe) {
                    Set-Content -LiteralPath $errFile -Value 'winapp.exe was not found in the guest.' -Encoding UTF8
                } else {
                    # Raw stream capture: a PowerShell redirect would reformat winapp's stderr.
                    $proc = Start-Process -FilePath $winappExe -ArgumentList {{argumentLine}} -WorkingDirectory $jobsRoot -RedirectStandardOutput $outFile -RedirectStandardError $errFile -NoNewWindow -Wait -PassThru
                    if ($null -ne $proc) {
                        $exitCode = $proc.ExitCode
                    }
                }
            } catch {
                $_.Exception.Message | Set-Content -LiteralPath $errFile -Encoding UTF8
            }
            Set-Content -LiteralPath $codeFile -Value $exitCode -Encoding UTF8
            """;
    }

    /// <summary>
    /// Joins arguments into one Windows command line using the quoting rules
    /// <c>CommandLineToArgvW</c> reverses. Windows PowerShell's <c>Start-Process</c> concatenates
    /// <c>-ArgumentList</c> entries with spaces and quotes nothing, so an argument containing a
    /// space (a window title such as <c>Task Manager</c>) would otherwise arrive as two.
    /// </summary>
    internal static string BuildWindowsArgumentLine(IReadOnlyList<string> arguments) =>
        string.Join(' ', arguments.Select(QuoteWindowsArgument));

    private static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        for (var index = 0; index < argument.Length; index++)
        {
            var backslashes = 0;
            while (index < argument.Length && argument[index] == '\\')
            {
                backslashes++;
                index++;
            }

            if (index == argument.Length)
            {
                // Trailing backslashes precede the closing quote, so they must be doubled.
                quoted.Append('\\', backslashes * 2);
                break;
            }

            if (argument[index] == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                quoted.Append('\\', backslashes).Append(argument[index]);
            }
        }

        return quoted.Append('"').ToString();
    }

    /// <summary>
    /// Host-side install script: opens one PowerShell Direct session with the stored guest
    /// credential, copies the verified archive in with <c>Copy-Item -ToSession</c> (the
    /// network-free path), and extracts it at <see cref="GuestInstallRoot"/>. Credential and
    /// path values are embedded as base64 so quoting can never break the script.
    /// </summary>
    internal static string BuildHostCopyScript(
        Guid machineId,
        string userName,
        string password,
        string archivePath)
    {
        var encodedUser = Convert.ToBase64String(Encoding.UTF8.GetBytes(userName));
        var encodedPassword = Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
        var encodedArchivePath = Convert.ToBase64String(Encoding.UTF8.GetBytes(archivePath));
        return $$"""
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $WarningPreference = 'SilentlyContinue'
            $env:WINAPP_CLI_TELEMETRY_OPTOUT = '1'
            function Decode([string]$Value) {
                [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
            }
            try {
                $userName = Decode '{{encodedUser}}'
                $password = ConvertTo-SecureString (Decode '{{encodedPassword}}') -AsPlainText -Force
                $credential = [System.Management.Automation.PSCredential]::new($userName, $password)
                $session = New-PSSession -VMId ([Guid]'{{machineId:D}}') -Credential $credential -ErrorAction Stop
                try {
                    Invoke-Command -Session $session -ErrorAction Stop -ScriptBlock {
                        $ErrorActionPreference = 'Stop'
                        New-Item -ItemType Directory -Force -Path '{{GuestInstallRoot}}' | Out-Null
                    }
                    Copy-Item -LiteralPath (Decode '{{encodedArchivePath}}') -Destination '{{GuestInstallRoot}}\winapp-install.zip' -ToSession $session -Force -ErrorAction Stop
                    $remoteExe = Invoke-Command -Session $session -ErrorAction Stop -ScriptBlock {
                        $ErrorActionPreference = 'Stop'
                        Expand-Archive -LiteralPath '{{GuestInstallRoot}}\winapp-install.zip' -DestinationPath '{{GuestInstallRoot}}' -Force
                        Remove-Item -LiteralPath '{{GuestInstallRoot}}\winapp-install.zip' -Force -ErrorAction SilentlyContinue
                        {{ResolveWinappSnippet}}
                        $winappExe
                    }
                    if ([string]::IsNullOrWhiteSpace([string]$remoteExe)) {
                        throw 'The winapp archive did not contain winapp.exe.'
                    }
                    @{ success = $true; exePath = [string]$remoteExe } | ConvertTo-Json -Compress
                } finally {
                    Remove-PSSession -Session $session -ErrorAction SilentlyContinue
                }
            } catch {
                @{ success = $false; error = $_.Exception.Message } | ConvertTo-Json -Compress
            }
            """;
    }

    /// <summary>Single-quotes data with the same delimiter escaping as host operations.</summary>
    internal static string QuotePowerShellLiteral(string value) =>
        PowerShellRunner.Literal(value);

    /// <summary>UTF-16LE base64 for <c>powershell.exe -EncodedCommand</c>.</summary>
    internal static string EncodePowerShellCommand(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    internal static string ValidateUiCommand(string? uiCommand)
    {
        var normalized = uiCommand?.Trim().ToLowerInvariant();
        if (normalized is { Length: > 0 }
            && RedirectedUiCommands.TryGetValue(normalized, out var redirect))
        {
            throw new ArgumentException(redirect, nameof(uiCommand));
        }

        if (string.IsNullOrEmpty(normalized)
            || !UiCommands.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Unknown winapp ui command '{uiCommand}'. Use one of: {string.Join(", ", UiCommands)}.",
                nameof(uiCommand));
        }

        return normalized;
    }

    internal static void ValidateUiArguments(IReadOnlyList<string> uiArguments)
    {
        if (uiArguments.Count > MaxUiArguments)
        {
            throw new ArgumentException(
                $"At most {MaxUiArguments} winapp ui arguments are supported.",
                nameof(uiArguments));
        }

        var totalLength = 0;
        foreach (var argument in uiArguments)
        {
            if (argument is null)
            {
                throw new ArgumentException(
                    "winapp ui arguments must not be null.",
                    nameof(uiArguments));
            }

            if (argument.Length > MaxUiArgumentLength)
            {
                throw new ArgumentException(
                    $"Each winapp ui argument must be at most {MaxUiArgumentLength} characters.",
                    nameof(uiArguments));
            }

            if (argument.Any(static character =>
                    char.IsControl(character) && character != '\t'))
            {
                throw new ArgumentException(
                    "winapp ui arguments must not contain control characters.",
                    nameof(uiArguments));
            }

            totalLength += argument.Length;
        }

        if (totalLength > MaxTotalUiArgumentLength)
        {
            throw new ArgumentException(
                $"winapp ui arguments must total at most {MaxTotalUiArgumentLength} characters.",
                nameof(uiArguments));
        }
    }

    internal static int ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(600))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "The winapp ui timeout must be between 1 and 600 seconds.");
        }

        return Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
    }

    /// <summary>
    /// Reassembles the chunked base64 result the guest emitted between the envelope markers.
    /// Whitespace is stripped first because the broker's <c>Out-String</c> rendering re-wraps
    /// lines.
    /// </summary>
    internal static string? ExtractGuestEnvelopeJson(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var begin = output.LastIndexOf(EnvelopeBeginMarker, StringComparison.Ordinal);
        if (begin < 0)
        {
            return null;
        }

        var payloadStart = begin + EnvelopeBeginMarker.Length;
        var end = output.IndexOf(EnvelopeEndMarker, payloadStart, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        var builder = new StringBuilder(end - payloadStart);
        for (var index = payloadStart; index < end; index++)
        {
            if (!char.IsWhiteSpace(output[index]))
            {
                builder.Append(output[index]);
            }
        }

        if (builder.Length == 0)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(builder.ToString()));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task<ProbeResult> ProbeAsync(Guid machineId, CancellationToken ct)
    {
        var result = await _runGuestScript(machineId, BuildProbeScript(), ct)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            return new(
                false,
                null,
                FirstLine(result.Error, "PowerShell Direct could not probe the guest for winapp."));
        }

        var envelope = ParseGuestEnvelope(result.Output);
        if (envelope is null)
        {
            return new(false, null, UnreadableResultError);
        }

        if (envelope.Status != "ok")
        {
            return new(
                false,
                null,
                FirstLine(envelope.Error, "The guest winapp probe failed."));
        }

        return new(
            envelope.Installed == true,
            NullIfWhiteSpace(envelope.Version),
            null);
    }

    /// <summary>
    /// Returns a verified local copy of the release archive. A cached archive whose SHA-256
    /// still matches its recorded digest is reused without touching the network, so setup keeps
    /// working offline once the release has been fetched.
    /// </summary>
    private async Task<(string ArchivePath, bool Downloaded)> EnsureHostArchiveAsync(
        CancellationToken ct)
    {
        await ArchiveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_cacheRoot);
            var archivePath = Path.Combine(_cacheRoot, ReleaseAssetName);
            var marker = await ReadMarkerAsync(ct).ConfigureAwait(false);
            if (marker is not null
                && File.Exists(archivePath)
                && await HashMatchesAsync(archivePath, marker.Digest, ct).ConfigureAwait(false))
            {
                return (archivePath, false);
            }

            var release = await GetLatestReleaseAsync(ct).ConfigureAwait(false);
            var downloadPath = Path.Combine(
                _cacheRoot,
                $".winapp-download-{Guid.NewGuid():N}.zip");
            try
            {
                using (var response = await _httpClient.GetAsync(
                           release.DownloadUri,
                           HttpCompletionOption.ResponseHeadersRead,
                           ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(ct)
                        .ConfigureAwait(false);
                    await using var destination = new FileStream(
                        downloadPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await source.CopyToAsync(destination, ct).ConfigureAwait(false);
                }

                if (!await HashMatchesAsync(downloadPath, release.Digest, ct).ConfigureAwait(false))
                {
                    throw new InvalidDataException(
                        "winapp download failed SHA-256 verification against the published digest.");
                }

                File.Move(downloadPath, archivePath, overwrite: true);
            }
            finally
            {
                TryDeleteFile(downloadPath);
            }

            await File.WriteAllTextAsync(
                MarkerPath,
                JsonSerializer.Serialize(new WinappReleaseMarker(
                    release.TagName,
                    release.Digest,
                    release.DownloadUri.AbsoluteUri)),
                ct).ConfigureAwait(false);
            return (archivePath, true);
        }
        finally
        {
            ArchiveGate.Release();
        }
    }

    private string MarkerPath => Path.Combine(_cacheRoot, ".release.json");

    private async Task<WinappReleaseMarker?> ReadMarkerAsync(CancellationToken ct)
    {
        if (!File.Exists(MarkerPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(MarkerPath);
            var marker = await JsonSerializer.DeserializeAsync<WinappReleaseMarker>(
                stream,
                cancellationToken: ct).ConfigureAwait(false);
            return IsValidDigest(marker?.Digest) ? marker : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task<WinappRelease> GetLatestReleaseAsync(CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseUri, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct)
            .ConfigureAwait(false);
        var root = document.RootElement;
        var tagName = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tagName))
        {
            throw new InvalidDataException("The latest winapp release has no tag name.");
        }

        JsonElement? selectedAsset = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (string.Equals(name, ReleaseAssetName, StringComparison.OrdinalIgnoreCase))
            {
                selectedAsset = asset;
                break;
            }
        }

        if (selectedAsset is not { } selected)
        {
            throw new InvalidDataException(
                $"The latest winapp release has no {ReleaseAssetName} asset.");
        }

        var downloadUrl = selected.GetProperty("browser_download_url").GetString();
        var digest = selected.TryGetProperty("digest", out var digestValue)
            ? digestValue.GetString()
            : null;
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri))
        {
            throw new InvalidDataException(
                "The latest winapp release asset is missing its download URL.");
        }

        if (!IsValidDigest(digest))
        {
            throw new InvalidDataException(
                "The latest winapp release asset has no SHA-256 digest.");
        }

        return new WinappRelease(tagName, downloadUri, digest);
    }

    private static bool IsValidDigest([NotNullWhen(true)] string? digest) =>
        digest is not null
        && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
        && digest.Length == "sha256:".Length + 64;

    private static async Task<bool> HashMatchesAsync(
        string filePath,
        string digest,
        CancellationToken ct)
    {
        if (!IsValidDigest(digest))
        {
            return false;
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        var expectedHash = digest["sha256:".Length..];
        return actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A verified install is usable even if Windows has not released the temp file yet.
        }
        catch (UnauthorizedAccessException)
        {
            // Leave the temporary file for a later OS cleanup instead of failing the install.
        }
    }

    private static GuestUiEnvelope? ParseGuestEnvelope(string? output)
    {
        var json = ExtractGuestEnvelopeJson(output);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GuestUiEnvelope>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The broker's install step: runs the host copy script (a PowerShell Direct session,
    /// <c>Copy-Item -ToSession</c>, and the guest-side extract) and maps its envelope. Lives
    /// here beside the script builder so the broker and this service can never drift; the
    /// caller supplies an already-resolved guest credential.
    /// </summary>
    internal static async Task<HyperVGuestCommandResult> RunHostCopyAsync(
        Guid machineId,
        string userName,
        string password,
        string archivePath,
        CancellationToken ct)
    {
        var script = BuildHostCopyScript(machineId, userName, password, archivePath);
        var result = await RunHostPowerShellAsync(script, ct).ConfigureAwait(false);
        var envelope = ParseHostCopyEnvelope(result.StandardOutput);
        if (envelope?.Success == true)
        {
            return new(true, envelope.ExePath, null);
        }

        return new(
            false,
            null,
            FirstLine(
                envelope?.Error ?? FirstHostError(result),
                "winapp could not be copied into the guest."));
    }

    /// <summary>
    /// The broker's general file-copy step (hyperv_copy): one PowerShell Direct session,
    /// <c>Copy-Item -ToSession/-FromSession</c> in either direction, with the destination
    /// directory pre-created (Copy-Item has no -CreateFullPath). Returns the copied byte
    /// count in the envelope so the tool can report it. The caller supplies an
    /// already-resolved guest credential; both paths are caller-supplied (unlike the fixed
    /// install path) and validated for shape at the protocol boundary.
    /// </summary>
    internal static async Task<HyperVGuestCommandResult> RunHostFileCopyAsync(
        Guid machineId,
        string userName,
        string password,
        string source,
        string destination,
        bool fromGuest,
        CancellationToken ct)
    {
        var script = BuildHostFileCopyScript(machineId, userName, password, source, destination, fromGuest);
        var result = await RunHostPowerShellAsync(script, ct).ConfigureAwait(false);
        var envelope = ParseHostCopyEnvelope(result.StandardOutput);
        if (envelope?.Success == true)
        {
            return new(true, envelope.ExePath, null);
        }

        return new(
            false,
            null,
            FirstLine(
                envelope?.Error ?? FirstHostError(result),
                "The file could not be copied."));
    }

    /// <summary>
    /// Host-side copy script: one PowerShell Direct session with the guest credential, a
    /// direction-appropriate <c>Copy-Item</c>, and a size probe of the SOURCE so the tool can
    /// report bytes and a directive fires when the source is missing. The <c>exePath</c>
    /// envelope field carries the byte count (reused rather than widening the shared
    /// envelope). All values are base64-embedded so quoting can never break the script.
    /// </summary>
    internal static string BuildHostFileCopyScript(
        Guid machineId,
        string userName,
        string password,
        string source,
        string destination,
        bool fromGuest)
    {
        var encodedUser = Convert.ToBase64String(Encoding.UTF8.GetBytes(userName));
        var encodedPassword = Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
        var encodedSource = Convert.ToBase64String(Encoding.UTF8.GetBytes(source));
        var encodedDestination = Convert.ToBase64String(Encoding.UTF8.GetBytes(destination));

        // The two directions differ only in which side owns each path: for to_guest the
        // source is probed and the destination directory created HOST-side (inline .NET),
        // and Copy-Item pushes with -ToSession; for from_guest both are done GUEST-side via
        // Invoke-Command and Copy-Item pulls with -FromSession.
        var probeAndPrepare = fromGuest
            ? """
                    $meta = Invoke-Command -Session $session -ErrorAction Stop -ScriptBlock {
                        param($src)
                        $ErrorActionPreference = 'Stop'
                        $item = Get-Item -LiteralPath $src -ErrorAction Stop
                        if ($item.PSIsContainer) { $bytes = (Get-ChildItem -LiteralPath $src -Recurse -File | Measure-Object -Property Length -Sum).Sum }
                        else { $bytes = $item.Length }
                        [pscustomobject]@{ IsDir = [bool]$item.PSIsContainer; Bytes = [int64]$bytes }
                    } -ArgumentList $sourcePath
                    $destDir = Split-Path -Parent $destinationPath
                    if ($destDir) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
            """
            : """
                    $item = Get-Item -LiteralPath $sourcePath -ErrorAction Stop
                    if ($item.PSIsContainer) { $bytes = (Get-ChildItem -LiteralPath $sourcePath -Recurse -File | Measure-Object -Property Length -Sum).Sum }
                    else { $bytes = $item.Length }
                    $meta = [pscustomobject]@{ IsDir = [bool]$item.PSIsContainer; Bytes = [int64]$bytes }
                    Invoke-Command -Session $session -ErrorAction Stop -ScriptBlock {
                        param($dest)
                        $ErrorActionPreference = 'Stop'
                        $destDir = Split-Path -Parent $dest
                        if ($destDir) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
                    } -ArgumentList $destinationPath
            """;

        var copyLine = fromGuest
            ? "Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -FromSession $session -Force -ErrorAction Stop @recurse"
            : "Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -ToSession $session -Force -ErrorAction Stop @recurse";

        return $$"""
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $WarningPreference = 'SilentlyContinue'
            $env:WINAPP_CLI_TELEMETRY_OPTOUT = '1'
            function Decode([string]$Value) {
                [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
            }
            try {
                $userName = Decode '{{encodedUser}}'
                $password = ConvertTo-SecureString (Decode '{{encodedPassword}}') -AsPlainText -Force
                $credential = [System.Management.Automation.PSCredential]::new($userName, $password)
                $sourcePath = Decode '{{encodedSource}}'
                $destinationPath = Decode '{{encodedDestination}}'
                $session = New-PSSession -VMId ([Guid]'{{machineId:D}}') -Credential $credential -ErrorAction Stop
                try {
            {{probeAndPrepare}}
                    $recurse = @{}
                    if ($meta.IsDir) { $recurse['Recurse'] = $true }
                    {{copyLine}}
                    @{ success = $true; exePath = [string]$meta.Bytes } | ConvertTo-Json -Compress
                } finally {
                    Remove-PSSession -Session $session -ErrorAction SilentlyContinue
                }
            } catch {
                @{ success = $false; error = $_.Exception.Message } | ConvertTo-Json -Compress
            }
            """;
    }

    private static HostCopyEnvelope? ParseHostCopyEnvelope(string output)
    {
        var json = output.Trim();
        if (json.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HostCopyEnvelope>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FirstHostError(HyperVService.PowerShellResult result)
    {
        var error = result.StandardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return error ?? $"Windows PowerShell exited with code {result.ExitCode}.";
    }

    private static async Task<HyperVService.PowerShellResult> RunHostPowerShellAsync(
        string script,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(-1, string.Empty, "Hyper-V is only available on Windows.");
        }

        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(EncodePowerShellCommand(script));

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new(-1, string.Empty, "Could not start Windows PowerShell.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                throw;
            }

            return new(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new(-1, string.Empty, ex.Message);
        }
    }

    private static void ValidateMachineId(Guid machineId)
    {
        if (machineId == Guid.Empty)
        {
            throw new ArgumentException("A Hyper-V machine ID is required.", nameof(machineId));
        }
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FirstLine(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();

    private static HttpClient CreateHttpClient()
    {
        // Generous for a release zip on a slow line, but finite: an infinite timeout let one
        // hung GitHub download stall the tool call until the user gave up and stopped the turn.
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HyperVControl/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private readonly record struct ProbeResult(bool Installed, string? Version, string? Error);

    private sealed record WinappRelease(string TagName, Uri DownloadUri, string Digest);

    private sealed record WinappReleaseMarker(string TagName, string Digest, string DownloadUrl);

    private sealed class GuestUiEnvelope
    {
        public string? Status { get; init; }

        public int? ExitCode { get; init; }

        public string? Stdout { get; init; }

        public string? Stderr { get; init; }

        public string? Error { get; init; }

        public bool? Installed { get; init; }

        public string? Version { get; init; }
    }

    private sealed class HostCopyEnvelope
    {
        public bool Success { get; init; }

        public string? ExePath { get; init; }

        public string? Error { get; init; }
    }
}

/// <summary>One stored Windows guest credential handed to the setup step's file copy.</summary>
/// <summary>Result of ensuring winapp is installed inside a guest.</summary>
public sealed record HyperVGuestUiSetupResult(
    bool Success,
    bool Installed,
    string? Version,
    bool Downloaded,
    string? Error);

/// <summary>Output of one winapp ui command executed in the guest's interactive session.</summary>
public sealed record HyperVGuestUiRunResult(
    bool Success,
    int? ExitCode,
    string? Output,
    string? ErrorOutput,
    string? Error);
