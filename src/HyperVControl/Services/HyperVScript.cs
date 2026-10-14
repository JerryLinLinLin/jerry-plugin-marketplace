using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HyperVControl.Services;

/// <summary>A Hyper-V host operation that failed inside Windows PowerShell, carrying the uniform error envelope.</summary>
internal sealed class HyperVOperationException(string message, string errorType, string? errorId = null, string? category = null) : Exception(message)
{
    /// <summary>Stable classification: InvalidVmState, NotFound, AmbiguousName, AccessDenied, InvalidArgument,
    /// HyperVNotInstalled, HyperVUnavailable, InvalidResponse or HyperVOperationFailed.</summary>
    public string ErrorType { get; } = errorType;
    /// <summary>PowerShell's FullyQualifiedErrorId, when the failure came from a cmdlet.</summary>
    public string? ErrorId { get; } = errorId;
    public string? Category { get; } = category;
}

/// <summary>One PowerShell argument rendered as script text. Strings always become single-quoted literals.</summary>
internal readonly record struct PsValue(string Text)
{
    public static PsValue Raw(string expression) => new(expression);
    public static PsValue Of(string value) => new(PowerShellRunner.Literal(value));
    public static PsValue Of(int value) => new(value.ToString(CultureInfo.InvariantCulture));
    public static PsValue Of(long value) => new(value.ToString(CultureInfo.InvariantCulture));
    public static PsValue Of(bool value) => new(value ? "$true" : "$false");
    public static PsValue Of(Guid value) => new($"([guid]'{value:D}')");
    public static PsValue Megabytes(long value) => new($"([long]{value.ToString(CultureInfo.InvariantCulture)}*1MB)");
    public static PsValue Gigabytes(long value) => new($"([long]{value.ToString(CultureInfo.InvariantCulture)}*1GB)");
    public static PsValue Token(string value, params string[] allowed) => allowed.Contains(value, StringComparer.Ordinal)
        ? new(value) : throw new ArgumentException($"Invalid value '{value}'. Use one of: {string.Join(", ", allowed)}.");
    public bool IsBoolean => Text is "$true" or "$false";
    public override string ToString() => Text;
}

/// <summary>
/// Builds one host-side Windows PowerShell script: hardened module bootstrap, VM resolution by exact
/// name or ID inside the script (one process per tool call), state assertions, typed parameter
/// rendering, and a uniform JSON envelope that <see cref="PowerShellRunner.InvokeAsync"/> unwraps.
/// </summary>
internal sealed class HyperVScript
{
    /// <summary>Test seam: a module manifest or directory imported instead of the system Hyper-V module.</summary>
    public const string ModulePathVariable = "HYPERV_CONTROL_MODULE_PATH";
    internal const string Sentinel = "##HYPERV-CONTROL##";

    private readonly StringBuilder body = new();
    private string result = "$null";

    /// <summary>
    /// Shared by every host script, including the typed <c>HyperVService</c> envelopes: pins the module
    /// path, imports the inbox modules explicitly, and defines the control helpers.
    /// </summary>
    internal const string ModuleBootstrap =
        """
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $WarningPreference = 'SilentlyContinue'
        $PSModuleAutoLoadingPreference = 'None'

        $systemModuleRoot = [System.IO.Path]::Combine($PSHOME, 'Modules')
        $env:PSModulePath = $systemModuleRoot
        Import-Module -Name ([System.IO.Path]::Combine($systemModuleRoot, 'Microsoft.PowerShell.Management', 'Microsoft.PowerShell.Management.psd1')) -ErrorAction Stop
        Import-Module -Name ([System.IO.Path]::Combine($systemModuleRoot, 'Microsoft.PowerShell.Utility', 'Microsoft.PowerShell.Utility.psd1')) -ErrorAction Stop
        Import-Module -Name ([System.IO.Path]::Combine($systemModuleRoot, 'CimCmdlets', 'CimCmdlets.psd1')) -ErrorAction SilentlyContinue

        function Test-ControlAuthorization {
            try {
                $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
                $principal = [Security.Principal.WindowsPrincipal]::new($identity)
                $hyperVAdministrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-578')
                return $principal.IsInRole($hyperVAdministrators) -or
                    $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            } catch {
                return $false
            }
        }

        # Imports the Hyper-V module, or a test module named by HYPERV_CONTROL_MODULE_PATH, and reports
        # the host capability without throwing. The vmms check is skipped for test modules.
        function Import-ControlHyperV {
            $override = $env:HYPERV_CONTROL_MODULE_PATH
            if (-not [string]::IsNullOrWhiteSpace($override)) {
                Import-Module -Name $override -Force -ErrorAction Stop
                return @{ state = 'available'; errorType = $null; detail = $null }
            }
            $hyperVModuleRoot = [System.IO.Path]::Combine($systemModuleRoot, 'Hyper-V')
            $manifests = if ([System.IO.Directory]::Exists($hyperVModuleRoot)) {
                @([System.IO.Directory]::GetFiles($hyperVModuleRoot, 'Hyper-V.psd1', [System.IO.SearchOption]::AllDirectories))
            } else { @() }
            if ($manifests.Count -eq 0) {
                return @{ state = 'notInstalled'; errorType = 'HyperVNotInstalled'; detail = 'The Hyper-V management tools are not installed.' }
            }
            $vmms = Microsoft.PowerShell.Management\Get-Service -Name 'vmms' -ErrorAction SilentlyContinue
            if ($null -eq $vmms) {
                return @{ state = 'notInstalled'; errorType = 'HyperVNotInstalled'; detail = 'The Hyper-V Virtual Machine Management service is not installed.' }
            }
            # A stopped service fails every Hyper-V cmdlet with the same "object was not found"
            # record one unreadable machine produces, so it is ruled out here, by name.
            if ($vmms.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
                return @{ state = 'unavailable'; errorType = 'HyperVUnavailable'; detail = 'The Hyper-V Virtual Machine Management service is not running.' }
            }
            try {
                Import-Module -Name ($manifests | Microsoft.PowerShell.Utility\Sort-Object -Descending | Microsoft.PowerShell.Utility\Select-Object -First 1) -ErrorAction Stop
            } catch {
                $denied = $_.CategoryInfo.Category -eq [System.Management.Automation.ErrorCategory]::PermissionDenied -or $_.Exception -is [UnauthorizedAccessException]
                return @{ state = $(if ($denied) { 'accessDenied' } else { 'unavailable' }); errorType = $(if ($denied) { 'AccessDenied' } else { 'HyperVUnavailable' }); detail = $_.Exception.Message }
            }
            return @{ state = 'available'; errorType = $null; detail = $null }
        }

        function Stop-Control([string]$Type, [string]$Message) {
            $Global:HyperVControlErrorType = $Type
            throw [System.InvalidOperationException]::new($Message)
        }

        function Get-ControlErrorType($Record) {
            $id = [string]$Record.FullyQualifiedErrorId
            $category = [string]$Record.CategoryInfo.Category
            $exception = $Record.Exception
            if ($exception -is [UnauthorizedAccessException] -or $category -eq 'PermissionDenied') { return 'AccessDenied' }
            if ($id -like 'InvalidState,*') { return 'InvalidVmState' }
            if ($category -eq 'ObjectNotFound' -or $id -like 'ObjectNotFound,*') { return 'NotFound' }
            if ($category -eq 'InvalidArgument' -or $exception -is [System.ArgumentException] -or $exception -is [System.Management.Automation.ParameterBindingException]) { return 'InvalidArgument' }
            if ($exception.GetType().FullName -eq 'Microsoft.HyperV.PowerShell.VirtualizationException' -and -not (Test-ControlAuthorization)) { return 'AccessDenied' }
            return 'HyperVOperationFailed'
        }

        # Exact name (case-insensitive) or GUID. Ambiguous names fail so that duplicate VM names,
        # which Hyper-V allows, never select the wrong machine.
        function Resolve-ControlVm([string]$Machine) {
            $id = [guid]::Empty
            if ([guid]::TryParse($Machine, [ref]$id)) {
                return (Hyper-V\Get-VM -Id $id -ErrorAction Stop)
            }
            $found = @(Hyper-V\Get-VM -ErrorAction Stop | Microsoft.PowerShell.Core\Where-Object { $_.Name -eq $Machine })
            if ($found.Count -eq 1) { return $found[0] }
            if ($found.Count -eq 0) { Stop-Control 'NotFound' ("VM '" + $Machine + "' not found. Call hyperv_list to get exact names/IDs.") }
            Stop-Control 'AmbiguousName' 'VM name is ambiguous. Use its ID.'
        }

        function Assert-ControlVmState($Vm, [string[]]$States) {
            $current = [string]$Vm.State
            if ($States -notcontains $current) {
                Stop-Control 'InvalidVmState' ('This operation requires the VM to be ' + ($States -join ' or ') + ' (current state: ' + $current + ').')
            }
        }
        """;

    /// <summary>Resolves <c>$vm</c> from an exact name or GUID inside the script.</summary>
    public HyperVScript Vm(string machine) => Line("$vm = Resolve-ControlVm " + PowerShellRunner.Literal(machine));
    public HyperVScript Vm(Guid id) => Vm(id.ToString("D"));

    /// <summary>Fails with <c>InvalidVmState</c> unless <c>$vm</c> is in one of the given states.</summary>
    public HyperVScript RequireState(params string[] states) =>
        Line("Assert-ControlVmState $vm @(" + string.Join(", ", states.Select(PowerShellRunner.Literal)) + ")");
    public HyperVScript RequireOff() => RequireState("Off");

    public HyperVScript Line(string text) { body.Append(text).Append('\n'); return this; }

    /// <summary>
    /// Appends one command. Parameters with a null value are omitted; booleans render as
    /// <c>-Name:$true</c> so switches and bool parameters both bind; strings are always literals.
    /// </summary>
    public HyperVScript Cmd(string command, params (string Name, PsValue? Value)[] parameters)
    {
        var line = new StringBuilder(command);
        foreach (var (name, value) in parameters)
        {
            if (value is null) continue;
            line.Append(" -").Append(name).Append(value.Value.IsBoolean ? ':' : ' ').Append(value.Value.Text);
        }
        return Line(line.ToString());
    }

    /// <summary>The expression whose value becomes the tool result (serialized to JSON, depth 12).</summary>
    public HyperVScript Returns(string expression) { result = expression; return this; }

    public string Body => body.ToString();

    public override string ToString() => ModuleBootstrap + "\n" + Envelope(body.ToString(), result);

    private static string Envelope(string body, string result) =>
        "$Global:HyperVControlErrorType = $null\n"
        + "try {\n"
        + "$__status = Import-ControlHyperV\n"
        + "if ($__status.state -ne 'available') { Stop-Control $__status.errorType $__status.detail }\n"
        + body
        + "$__result = " + result + "\n"
        + "$__json = @{ ok = $true; data = $__result } | Microsoft.PowerShell.Utility\\ConvertTo-Json -Depth 12 -Compress\n"
        + "} catch {\n"
        + "$__type = if ($Global:HyperVControlErrorType) { $Global:HyperVControlErrorType } else { Get-ControlErrorType $_ }\n"
        + "$__id = if ($Global:HyperVControlErrorType) { $null } else { [string]$_.FullyQualifiedErrorId }\n"
        + "$__json = @{ ok = $false; error = [string]$_.Exception.Message; errorType = $__type; errorId = $__id; category = [string]$_.CategoryInfo.Category } | Microsoft.PowerShell.Utility\\ConvertTo-Json -Compress\n"
        + "}\n"
        + "[Console]::Out.WriteLine(\"`n" + Sentinel + "\" + $__json)\n";

    /// <summary>Unwraps the envelope written by <see cref="Envelope"/>; throws the uniform exception on failure.</summary>
    internal static JsonElement Unwrap(ProcessResult result)
    {
        var marker = result.Stdout.LastIndexOf(Sentinel, StringComparison.Ordinal);
        if (marker < 0)
        {
            var error = result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            throw new HyperVOperationException(error ?? $"Windows PowerShell exited with code {result.ExitCode} without a result.", "InvalidResponse");
        }
        var json = result.Stdout[(marker + Sentinel.Length)..].Trim();
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException ex) { throw new HyperVOperationException("PowerShell returned an invalid JSON result: " + json[..Math.Min(2000, json.Length)], "InvalidResponse", null, ex.Message); }
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new HyperVOperationException(
                root.TryGetProperty("error", out var e) ? e.GetString() ?? "Unknown error" : "Unknown error",
                root.TryGetProperty("errorType", out var t) ? t.GetString() ?? "HyperVOperationFailed" : "HyperVOperationFailed",
                root.TryGetProperty("errorId", out var i) ? i.GetString() : null,
                root.TryGetProperty("category", out var c) ? c.GetString() : null);
        return root.TryGetProperty("data", out var data) ? data : default;
    }
}
