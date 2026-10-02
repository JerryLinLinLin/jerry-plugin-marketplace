// Extracted and adapted from RaivenX b5971dda1fa2050dde36b8b22fda9726df92cb48. See THIRD-PARTY-NOTICES.md.
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HyperVControl.Extracted;

/// <summary>
/// Enumerates and controls virtual machines on the local Hyper-V host through the Microsoft
/// Hyper-V PowerShell module. The service never requests elevation; callers receive an explicit
/// capability state when the current account is not authorized.
/// </summary>
public sealed class HyperVService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string ScriptPreamble =
        """
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $WarningPreference = 'SilentlyContinue'
        $PSModuleAutoLoadingPreference = 'None'

        $systemModuleRoot = [System.IO.Path]::Combine($PSHOME, 'Modules')
        $env:PSModulePath = $systemModuleRoot
        Import-Module -Name ([System.IO.Path]::Combine(
            $systemModuleRoot,
            'Microsoft.PowerShell.Management',
            'Microsoft.PowerShell.Management.psd1')) -ErrorAction Stop
        Import-Module -Name ([System.IO.Path]::Combine(
            $systemModuleRoot,
            'Microsoft.PowerShell.Utility',
            'Microsoft.PowerShell.Utility.psd1')) -ErrorAction Stop

        function Write-Result(
            [string]$Status,
            [string]$Detail,
            [object[]]$Machines,
            [string]$OperationError,
            [Nullable[bool]]$SecureBootEnabled = $null) {
            [pscustomobject]@{
                hostName = [Environment]::MachineName
                status = $Status
                detail = $Detail
                operationError = $OperationError
                secureBootEnabled = $SecureBootEnabled
                machines = @($Machines)
            } | Microsoft.PowerShell.Utility\ConvertTo-Json -Compress -Depth 6
        }

        function Write-CheckpointResult(
            [string]$Status,
            [string]$Detail,
            [object[]]$Checkpoints,
            [string]$OperationError,
            [string]$CurrentParentCheckpointId = $null) {
            [pscustomobject]@{
                hostName = [Environment]::MachineName
                status = $Status
                detail = $Detail
                operationError = $OperationError
                currentParentCheckpointId = $CurrentParentCheckpointId
                checkpoints = @($Checkpoints)
            } | Microsoft.PowerShell.Utility\ConvertTo-Json -Compress -Depth 6
        }

        function Test-HyperVAuthorization {
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

        function Write-CapabilityFailure([System.Management.Automation.ErrorRecord]$Record) {
            $permissionDenied = $Record.CategoryInfo.Category -eq
                    [System.Management.Automation.ErrorCategory]::PermissionDenied -or
                $Record.Exception -is [UnauthorizedAccessException] -or
                ($Record.Exception.GetType().FullName -eq
                    'Microsoft.HyperV.PowerShell.VirtualizationException' -and
                    -not (Test-HyperVAuthorization))
            $status = if ($permissionDenied) { 'accessDenied' } else { 'unavailable' }
            Write-Result $status $Record.Exception.Message @() $null
        }

        function Convert-Machine([object]$Vm) {
            $uptimeTicks = $null
            if ($Vm.State.ToString() -ne 'Off' -and $null -ne $Vm.Uptime) {
                $uptimeTicks = [long]$Vm.Uptime.Ticks
            }

            $operatingSystem = [string]$Vm.GuestOperatingSystem
            if ([string]::IsNullOrWhiteSpace($operatingSystem)) {
                $operatingSystem = $null
            }

            $version = [string]$Vm.Version
            if ([string]::IsNullOrWhiteSpace($version)) {
                $version = $null
            }

            [pscustomobject]@{
                id = $Vm.Id.ToString('D')
                name = [string]$Vm.Name
                state = $Vm.State.ToString()
                generation = if ($null -eq $Vm.Generation) { $null } else { [int]$Vm.Generation }
                memoryAssignedBytes = if ($null -eq $Vm.MemoryAssigned) {
                    $null
                } else {
                    [long]$Vm.MemoryAssigned
                }
                processorCount = if ($null -eq $Vm.ProcessorCount) {
                    $null
                } else {
                    [int]$Vm.ProcessorCount
                }
                uptimeTicks = $uptimeTicks
                operatingSystem = $operatingSystem
                version = $version
            }
        }

        function Convert-Checkpoint([object]$Checkpoint) {
            $parentCheckpointId = $null
            if ($null -ne $Checkpoint.ParentSnapshotId -and
                [Guid]$Checkpoint.ParentSnapshotId -ne [Guid]::Empty) {
                $parentCheckpointId = ([Guid]$Checkpoint.ParentSnapshotId).ToString('D')
            }

            [pscustomobject]@{
                id = ([Guid]$Checkpoint.Id).ToString('D')
                machineId = ([Guid]$Checkpoint.VMId).ToString('D')
                name = [string]$Checkpoint.Name
                creationTime = ([DateTime]$Checkpoint.CreationTime).ToUniversalTime().ToString('O')
                parentCheckpointId = $parentCheckpointId
                type = [string]$Checkpoint.SnapshotType
            }
        }

        $hyperVModuleRoot = Join-Path $systemModuleRoot 'Hyper-V'
        $hyperVManifests = if ([System.IO.Directory]::Exists($hyperVModuleRoot)) {
            @([System.IO.Directory]::GetFiles(
                $hyperVModuleRoot,
                'Hyper-V.psd1',
                [System.IO.SearchOption]::AllDirectories))
        } else {
            @()
        }

        if ($hyperVManifests.Count -eq 0) {
            Write-Result 'notInstalled' 'The Hyper-V management tools are not installed.' @() $null
            exit 0
        }

        $vmms = Microsoft.PowerShell.Management\Get-Service -Name 'vmms' -ErrorAction SilentlyContinue
        if ($null -eq $vmms) {
            Write-Result 'notInstalled' 'The Hyper-V Virtual Machine Management service is not installed.' @() $null
            exit 0
        }

        # A stopped service fails every Hyper-V cmdlet with the same "object was not found"
        # record one unreadable machine produces, so it is ruled out here, by name.
        if ($vmms.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
            Write-Result 'unavailable' 'The Hyper-V Virtual Machine Management service is not running.' @() $null
            exit 0
        }

        try {
            $hyperVManifest = $hyperVManifests |
                Sort-Object -Descending |
                Select-Object -First 1
            Import-Module -Name $hyperVManifest -ErrorAction Stop
        } catch {
            Write-CapabilityFailure $_
            exit 0
        }
        """;

    private readonly Func<string, CancellationToken, Task<PowerShellResult>> _runPowerShell;

    public HyperVService()
        : this(RunPowerShellAsync)
    {
    }

    internal HyperVService(Func<string, CancellationToken, Task<PowerShellResult>> runPowerShell)
    {
        _runPowerShell = runPowerShell ?? throw new ArgumentNullException(nameof(runPowerShell));
    }

    /// <summary>Returns local VM inventory or an explicit host capability failure.</summary>
    public async Task<HyperVInventory> GetInventoryAsync(CancellationToken ct = default)
    {
        var result = await _runPowerShell(BuildInventoryScript(), ct).ConfigureAwait(false);
        var envelope = ParseEnvelope(result);
        return new HyperVInventory(
            ResolveHostName(envelope.HostName),
            CreateCapability(envelope),
            ParseMachines(envelope.Machines));
    }

    /// <summary>Applies one explicit VM lifecycle transition and returns its updated state.</summary>
    public async Task<HyperVPowerOperationResult> ChangePowerStateAsync(
        Guid machineId,
        HyperVPowerAction action,
        CancellationToken ct = default)
    {
        ValidateMachineId(machineId);

        var result = await _runPowerShell(BuildPowerScript(machineId, action), ct)
            .ConfigureAwait(false);
        var envelope = ParseEnvelope(result);
        var capability = CreateCapability(envelope);
        var machine = ParseMachines(envelope.Machines).SingleOrDefault();
        var success = capability.IsAvailable
            && string.IsNullOrWhiteSpace(envelope.OperationError)
            && machine is not null;
        return new HyperVPowerOperationResult(
            success,
            capability,
            machine,
            success ? null : envelope.OperationError ?? capability.Detail);
    }

    /// <summary>
    /// Enables or disables Secure Boot for an Off Generation 2 virtual machine and verifies it.
    /// </summary>
    public async Task<HyperVFirmwareOperationResult> SetSecureBootAsync(
        Guid machineId,
        bool enabled,
        CancellationToken ct = default)
    {
        ValidateMachineId(machineId);

        var result = await _runPowerShell(BuildSecureBootScript(machineId, enabled), ct)
            .ConfigureAwait(false);
        var envelope = ParseEnvelope(result);
        var capability = CreateCapability(envelope);
        var machine = ParseMachines(envelope.Machines).SingleOrDefault();
        var success = capability.IsAvailable
            && string.IsNullOrWhiteSpace(envelope.OperationError)
            && machine is not null
            && envelope.SecureBootEnabled == enabled;
        var error = success
            ? null
            : envelope.OperationError
                ?? capability.Detail
                ?? "Hyper-V did not verify the requested Secure Boot setting.";
        return new(
            success,
            capability,
            machine,
            success ? envelope.SecureBootEnabled : null,
            error);
    }

    /// <summary>Returns the checkpoints that belong to one virtual machine.</summary>
    public async Task<HyperVCheckpointListResult> GetCheckpointsAsync(
        Guid machineId,
        CancellationToken ct = default)
    {
        ValidateMachineId(machineId);

        var result = await _runPowerShell(BuildCheckpointListScript(machineId), ct)
            .ConfigureAwait(false);
        var envelope = ParseEnvelope(result);
        var capability = CreateCapability(envelope);
        var checkpoints = ParseCheckpoints(envelope.Checkpoints);
        var success = capability.IsAvailable
            && string.IsNullOrWhiteSpace(envelope.OperationError);
        Guid? currentParentCheckpointId = null;
        if (Guid.TryParse(envelope.CurrentParentCheckpointId, out var parsedCurrentParent)
            && parsedCurrentParent != Guid.Empty)
        {
            currentParentCheckpointId = parsedCurrentParent;
        }

        return new(
            success,
            capability,
            success ? checkpoints : [],
            success ? null : envelope.OperationError ?? capability.Detail,
            success ? currentParentCheckpointId : null);
    }

    /// <summary>Creates a named checkpoint for one virtual machine.</summary>
    public async Task<HyperVCheckpointOperationResult> CreateCheckpointAsync(
        Guid machineId,
        string name,
        CancellationToken ct = default)
    {
        ValidateMachineId(machineId);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256)
        {
            throw new ArgumentException(
                "A checkpoint name between 1 and 256 characters is required.",
                nameof(name));
        }

        return await ExecuteCheckpointOperationAsync(
            BuildCreateCheckpointScript(machineId, name.Trim()),
            ct).ConfigureAwait(false);
    }

    /// <summary>Restores one checkpoint that belongs to the specified virtual machine.</summary>
    public async Task<HyperVCheckpointOperationResult> RestoreCheckpointAsync(
        Guid machineId,
        Guid checkpointId,
        CancellationToken ct = default)
    {
        ValidateMachineId(machineId);
        ValidateCheckpointId(checkpointId);
        return await ExecuteCheckpointOperationAsync(
            BuildRestoreCheckpointScript(machineId, checkpointId),
            ct).ConfigureAwait(false);
    }

    /// <summary>Deletes one checkpoint that belongs to the specified virtual machine.</summary>
    public async Task<HyperVCheckpointOperationResult> DeleteCheckpointAsync(
        Guid machineId,
        Guid checkpointId,
        CancellationToken ct = default)
    {
        ValidateMachineId(machineId);
        ValidateCheckpointId(checkpointId);
        return await ExecuteCheckpointOperationAsync(
            BuildDeleteCheckpointScript(machineId, checkpointId),
            ct).ConfigureAwait(false);
    }

    internal static string BuildInventoryScript() =>
        ScriptPreamble
        + """

          try {
              # Get-VM reports a machine it cannot read (a corrupted or critical saved state) as a
              # non-terminating error and keeps listing the rest. -ErrorAction Stop would turn
              # that first record into a failure of the whole inventory, so the errors are
              # collected instead and reported beside the listing. With no listing at all there
              # is nothing to report them beside, so the first one is the host's failure — which
              # is also how a denied token reports.
              $inventoryErrors = @()
              $vms = @(Hyper-V\Get-VM -ErrorAction SilentlyContinue -ErrorVariable inventoryErrors)
              if ($vms.Count -eq 0 -and $inventoryErrors.Count -gt 0) {
                  Write-CapabilityFailure $inventoryErrors[0]
                  exit 0
              }

              $machines = @($vms | ForEach-Object { Convert-Machine $_ })
              $detail = $null
              if ($inventoryErrors.Count -gt 0) {
                  $skipped = if ($inventoryErrors.Count -eq 1) {
                      "1 virtual machine couldn't be read and isn't listed."
                  } else {
                      "$($inventoryErrors.Count) virtual machines couldn't be read and aren't listed."
                  }
                  $detail = "$skipped $($inventoryErrors[0].Exception.Message)".Trim()
              }

              Write-Result 'available' $detail $machines $null
          } catch {
              Write-CapabilityFailure $_
          }
          """;

    internal static string BuildPowerScript(Guid machineId, HyperVPowerAction action)
    {
        // -Confirm:$false only suppresses ShouldProcess. Stop-VM also calls ShouldContinue,
        // which the Hyper-V module gates on -Force alone. Under the broker's -NonInteractive
        // host that prompt fails the operation. Start/Suspend/Resume do not define -Force.
        var command = action switch
        {
            HyperVPowerAction.Start => "$vm | Hyper-V\\Start-VM -Confirm:$false",
            HyperVPowerAction.Shutdown => "$vm | Hyper-V\\Stop-VM -Force -Confirm:$false",
            HyperVPowerAction.TurnOff => "$vm | Hyper-V\\Stop-VM -TurnOff -Force -Confirm:$false",
            HyperVPowerAction.Restart =>
                """
                $shutdownDeadline = [DateTime]::UtcNow.AddMinutes(5)
                $vm | Hyper-V\Stop-VM -Force -Confirm:$false
                while ($true) {
                    $vm = Hyper-V\Get-VM -Id $vm.Id -ErrorAction Stop
                    if ($vm.State.ToString() -eq 'Off') {
                        break
                    }
                    if ([DateTime]::UtcNow -ge $shutdownDeadline) {
                        throw 'The virtual machine did not shut down within five minutes.'
                    }
                    Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds 500
                }

                $vm | Hyper-V\Start-VM -Confirm:$false
                $startDeadline = [DateTime]::UtcNow.AddMinutes(1)
                while ($true) {
                    $vm = Hyper-V\Get-VM -Id $vm.Id -ErrorAction Stop
                    if ($vm.State.ToString() -eq 'Running') {
                        break
                    }
                    if ([DateTime]::UtcNow -ge $startDeadline) {
                        throw 'The virtual machine did not start within one minute.'
                    }
                    Microsoft.PowerShell.Utility\Start-Sleep -Milliseconds 500
                }
                """,
            HyperVPowerAction.Suspend => "$vm | Hyper-V\\Suspend-VM -Confirm:$false",
            HyperVPowerAction.Resume => "$vm | Hyper-V\\Resume-VM -Confirm:$false",
            HyperVPowerAction.Save => "$vm | Hyper-V\\Stop-VM -Save -Force -Confirm:$false",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };

        return ScriptPreamble
            + $$"""

              try {
                  $vm = Hyper-V\Get-VM -Id '{{machineId:D}}' -ErrorAction Stop
              } catch {
                  Write-CapabilityFailure $_
                  exit 0
              }

              try {
                  {{command}}
                  $updated = Hyper-V\Get-VM -Id '{{machineId:D}}' -ErrorAction Stop
                  Write-Result 'available' $null @((Convert-Machine $updated)) $null
              } catch {
                  Write-Result 'available' $null @() $_.Exception.Message
              }
              """;
    }

    internal static string BuildSecureBootScript(Guid machineId, bool enabled)
    {
        var setting = enabled ? "On" : "Off";
        var expected = enabled ? "$true" : "$false";
        return ScriptPreamble
            + $$"""

              try {
                  $vm = Hyper-V\Get-VM -Id '{{machineId:D}}' -ErrorAction Stop
              } catch {
                  Write-CapabilityFailure $_
                  exit 0
              }

              try {
                  if ([int]$vm.Generation -ne 2) {
                      throw 'Secure Boot is supported only for Generation 2 virtual machines.'
                  }
                  if ($vm.State.ToString() -ne 'Off') {
                      throw 'The virtual machine must be Off before changing Secure Boot.'
                  }

                  $vm |
                      Hyper-V\Set-VMFirmware `
                          -EnableSecureBoot {{setting}} `
                          -Confirm:$false `
                          -ErrorAction Stop
                  $firmware = $vm | Hyper-V\Get-VMFirmware -ErrorAction Stop
                  $secureBootEnabled = $firmware.SecureBoot.ToString() -eq 'On'
                  if ($secureBootEnabled -ne {{expected}}) {
                      throw 'Hyper-V did not apply the requested Secure Boot setting.'
                  }

                  $updated = Hyper-V\Get-VM -Id '{{machineId:D}}' -ErrorAction Stop
                  Write-Result `
                      'available' `
                      $null `
                      @((Convert-Machine $updated)) `
                      $null `
                      $secureBootEnabled
              } catch {
                  Write-Result 'available' $null @() $_.Exception.Message
              }
              """;
    }

    internal static string BuildCheckpointListScript(Guid machineId) =>
        ScriptPreamble
        + $$"""

          try {
              $vm = Hyper-V\Get-VM -Id '{{machineId:D}}' -ErrorAction Stop
          } catch {
              Write-CapabilityFailure $_
              exit 0
          }

          try {
              $checkpoints = @(
                  $vm |
                      Hyper-V\Get-VMSnapshot -ErrorAction Stop |
                      Microsoft.PowerShell.Utility\Sort-Object -Property CreationTime -Descending |
                      Microsoft.PowerShell.Core\ForEach-Object { Convert-Checkpoint $_ }
              )
              $currentParent = @(
                  Hyper-V\Get-VMSnapshot -ParentOf $vm -ErrorAction Stop
              ) | Microsoft.PowerShell.Utility\Select-Object -First 1
              $currentParentCheckpointId = if ($null -eq $currentParent) {
                  $null
              } else {
                  ([Guid]$currentParent.Id).ToString('D')
              }
              Write-CheckpointResult `
                  'available' `
                  $null `
                  $checkpoints `
                  $null `
                  $currentParentCheckpointId
          } catch {
              Write-CheckpointResult 'available' $null @() $_.Exception.Message
          }
          """;

    internal static string BuildCreateCheckpointScript(Guid machineId, string name)
    {
        var encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes(name));
        return ScriptPreamble
            + $$"""

              try {
                  $vm = Hyper-V\Get-VM -Id '{{machineId:D}}' -ErrorAction Stop
              } catch {
                  Write-CapabilityFailure $_
                  exit 0
              }

              try {
                  $checkpointName = [Text.Encoding]::UTF8.GetString(
                      [Convert]::FromBase64String('{{encodedName}}'))
                  $checkpoint = $vm |
                      Hyper-V\Checkpoint-VM -SnapshotName $checkpointName -Passthru -Confirm:$false -ErrorAction Stop
                  Write-CheckpointResult 'available' $null @((Convert-Checkpoint $checkpoint)) $null
              } catch {
                  Write-CheckpointResult 'available' $null @() $_.Exception.Message
              }
              """;
    }

    internal static string BuildRestoreCheckpointScript(Guid machineId, Guid checkpointId) =>
        BuildExistingCheckpointScript(
            machineId,
            checkpointId,
            "$checkpoint | Hyper-V\\Restore-VMSnapshot -Confirm:$false -ErrorAction Stop");

    internal static string BuildDeleteCheckpointScript(Guid machineId, Guid checkpointId) =>
        BuildExistingCheckpointScript(
            machineId,
            checkpointId,
            "$checkpoint | Hyper-V\\Remove-VMSnapshot -Confirm:$false -ErrorAction Stop");

    private static string BuildExistingCheckpointScript(
        Guid machineId,
        Guid checkpointId,
        string command) =>
        ScriptPreamble
        + $$"""

          try {
              $vm = Hyper-V\Get-VM -Id '{{machineId:D}}' -ErrorAction Stop
          } catch {
              Write-CapabilityFailure $_
              exit 0
          }

          try {
              $checkpoint = @(
                  $vm |
                      Hyper-V\Get-VMSnapshot -ErrorAction Stop |
                      Microsoft.PowerShell.Core\Where-Object {
                          $_.Id -eq [Guid]'{{checkpointId:D}}'
                      }
              ) | Microsoft.PowerShell.Utility\Select-Object -First 1
              if ($null -eq $checkpoint) {
                  throw [InvalidOperationException]::new(
                      'The checkpoint was not found for this virtual machine.')
              }

              $checkpointData = Convert-Checkpoint $checkpoint
              {{command}}
              Write-CheckpointResult 'available' $null @($checkpointData) $null
          } catch {
              Write-CheckpointResult 'available' $null @() $_.Exception.Message
          }
          """;

    private async Task<HyperVCheckpointOperationResult> ExecuteCheckpointOperationAsync(
        string script,
        CancellationToken ct)
    {
        var result = await _runPowerShell(script, ct).ConfigureAwait(false);
        var envelope = ParseEnvelope(result);
        var capability = CreateCapability(envelope);
        var checkpoint = ParseCheckpoints(envelope.Checkpoints).SingleOrDefault();
        var success = capability.IsAvailable
            && string.IsNullOrWhiteSpace(envelope.OperationError)
            && checkpoint is not null;
        return new(
            success,
            capability,
            checkpoint,
            success ? null : envelope.OperationError ?? capability.Detail);
    }

    private static void ValidateMachineId(Guid machineId)
    {
        if (machineId == Guid.Empty)
        {
            throw new ArgumentException("A Hyper-V machine ID is required.", nameof(machineId));
        }
    }

    private static void ValidateCheckpointId(Guid checkpointId)
    {
        if (checkpointId == Guid.Empty)
        {
            throw new ArgumentException(
                "A Hyper-V checkpoint ID is required.",
                nameof(checkpointId));
        }
    }

    private static HyperVEnvelope ParseEnvelope(PowerShellResult result)
    {
        var json = result.StandardOutput.Trim();
        if (json.Length == 0)
        {
            return UnavailableEnvelope(FirstError(result));
        }

        try
        {
            return JsonSerializer.Deserialize<HyperVEnvelope>(json, JsonOptions)
                ?? UnavailableEnvelope("Hyper-V returned an empty response.");
        }
        catch (JsonException)
        {
            return UnavailableEnvelope(
                result.ExitCode == 0
                    ? "Hyper-V returned an invalid response."
                    : FirstError(result));
        }
    }

    private static HyperVEnvelope UnavailableEnvelope(string detail) => new()
    {
        HostName = Environment.MachineName,
        Status = "unavailable",
        Detail = detail,
        Machines = [],
    };

    private static string FirstError(PowerShellResult result)
    {
        var error = result.StandardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return error ?? $"Windows PowerShell exited with code {result.ExitCode}.";
    }

    private static string ResolveHostName(string? hostName) =>
        string.IsNullOrWhiteSpace(hostName) ? Environment.MachineName : hostName;

    private static HyperVCapability CreateCapability(HyperVEnvelope envelope)
    {
        var state = envelope.Status?.Trim().ToLowerInvariant() switch
        {
            "available" => HyperVCapabilityState.Available,
            "notinstalled" => HyperVCapabilityState.NotInstalled,
            "accessdenied" => HyperVCapabilityState.AccessDenied,
            _ => HyperVCapabilityState.Unavailable,
        };
        return new HyperVCapability(state, NullIfWhiteSpace(envelope.Detail));
    }

    private static IReadOnlyList<HyperVMachine> ParseMachines(IReadOnlyList<HyperVMachineDto>? rows)
    {
        if (rows is null || rows.Count == 0)
        {
            return [];
        }

        var machines = new List<HyperVMachine>(rows.Count);
        foreach (var row in rows)
        {
            if (!Guid.TryParse(row.Id, out var id) || string.IsNullOrWhiteSpace(row.Name))
            {
                continue;
            }

            machines.Add(new HyperVMachine(
                id,
                row.Name.Trim(),
                ParseState(row.State),
                row.Generation is > 0 ? row.Generation : null,
                row.MemoryAssignedBytes is >= 0 ? row.MemoryAssignedBytes : null,
                row.ProcessorCount is > 0 ? row.ProcessorCount : null,
                row.UptimeTicks is >= 0 ? TimeSpan.FromTicks(row.UptimeTicks.Value) : null,
                NullIfWhiteSpace(row.OperatingSystem),
                NullIfWhiteSpace(row.Version)));
        }

        machines.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        return machines;
    }

    private static IReadOnlyList<HyperVCheckpoint> ParseCheckpoints(
        IReadOnlyList<HyperVCheckpointDto>? rows)
    {
        if (rows is null || rows.Count == 0)
        {
            return [];
        }

        var checkpoints = new List<HyperVCheckpoint>(rows.Count);
        foreach (var row in rows)
        {
            if (!Guid.TryParse(row.Id, out var id)
                || !Guid.TryParse(row.MachineId, out var machineId)
                || string.IsNullOrWhiteSpace(row.Name)
                || !DateTimeOffset.TryParse(
                    row.CreationTime,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                        | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var creationTime))
            {
                continue;
            }

            Guid? parentCheckpointId = null;
            if (Guid.TryParse(row.ParentCheckpointId, out var parsedParent)
                && parsedParent != Guid.Empty)
            {
                parentCheckpointId = parsedParent;
            }

            checkpoints.Add(new(
                id,
                machineId,
                row.Name.Trim(),
                creationTime,
                parentCheckpointId,
                NullIfWhiteSpace(row.Type)));
        }

        checkpoints.Sort(static (left, right) =>
            right.CreationTime.CompareTo(left.CreationTime));
        return checkpoints;
    }

    private static HyperVMachineState ParseState(string? state)
    {
        if (state?.EndsWith("Critical", StringComparison.OrdinalIgnoreCase) == true)
        {
            return HyperVMachineState.Critical;
        }

        return Enum.TryParse<HyperVMachineState>(state, ignoreCase: true, out var parsed)
            ? parsed
            : HyperVMachineState.Unknown;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<PowerShellResult> RunPowerShellAsync(
        string script,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new PowerShellResult(-1, string.Empty, "Hyper-V is only available on Windows.");
        }

        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
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
        startInfo.ArgumentList.Add(encodedCommand);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new PowerShellResult(-1, string.Empty, "Could not start Windows PowerShell.");
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

            return new PowerShellResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new PowerShellResult(-1, string.Empty, ex.Message);
        }
    }

    internal sealed record PowerShellResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class HyperVEnvelope
    {
        public string? HostName { get; init; }

        public string? Status { get; init; }

        public string? Detail { get; init; }

        public string? OperationError { get; init; }

        public string? CurrentParentCheckpointId { get; init; }

        public bool? SecureBootEnabled { get; init; }

        public IReadOnlyList<HyperVMachineDto>? Machines { get; init; }

        public IReadOnlyList<HyperVCheckpointDto>? Checkpoints { get; init; }
    }

    private sealed class HyperVMachineDto
    {
        public string? Id { get; init; }

        public string? Name { get; init; }

        public string? State { get; init; }

        public int? Generation { get; init; }

        public long? MemoryAssignedBytes { get; init; }

        public int? ProcessorCount { get; init; }

        public long? UptimeTicks { get; init; }

        public string? OperatingSystem { get; init; }

        public string? Version { get; init; }
    }

    private sealed class HyperVCheckpointDto
    {
        public string? Id { get; init; }

        public string? MachineId { get; init; }

        public string? Name { get; init; }

        public string? CreationTime { get; init; }

        public string? ParentCheckpointId { get; init; }

        public string? Type { get; init; }
    }
}
