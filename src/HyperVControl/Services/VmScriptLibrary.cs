namespace HyperVControl.Services;

/// <summary>
/// PowerShell functions shared by the lifecycle scripts: projections, the state gate, virtual disk
/// chains and references, removal planning, merge waiting and preflight checks. Constant text with no
/// user input; scripts include it after the module bootstrap, so it runs against the real or mock module.
/// </summary>
internal static class VmScriptLibrary
{
    internal const string DefaultSwitchId = "c08cb7b8-9b3c-408e-8e30-5e16a3aeb444";

    internal const string Functions =
        """
        function Get-ControlText($Value) { if ($null -eq $Value) { return $null }; return [string]$Value }

        function Get-ControlPathKey([string]$Path) {
            if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
            $full = $Path
            try { $full = [System.IO.Path]::GetFullPath($Path) } catch { }
            return $full.TrimEnd('\').ToLowerInvariant()
        }

        # The differencing chain of one virtual disk, the file itself first. A link that cannot be read
        # ends the chain with an entry carrying Error instead of failing the caller.
        function Get-ControlVhdChain([string]$Path) {
            $chain = New-Object System.Collections.ArrayList
            $seen = @{}
            $current = $Path
            while (-not [string]::IsNullOrWhiteSpace($current) -and $chain.Count -lt 64) {
                $key = Get-ControlPathKey $current
                if ($seen.ContainsKey($key)) { break }
                $seen[$key] = $true
                try { $disk = Get-VHD -Path $current -ErrorAction Stop }
                catch {
                    [void]$chain.Add([pscustomobject]@{ Path = $current; VhdFormat = $null; VhdType = $null; ParentPath = $null; Size = $null; FileSize = $null; Error = [string]$_.Exception.Message })
                    break
                }
                [void]$chain.Add($disk)
                $current = [string]$disk.ParentPath
            }
            return $chain.ToArray()
        }

        function ConvertTo-ControlVhdLink($Disk) {
            $problem = $null
            if ($null -ne $Disk.PSObject.Properties['Error']) { $problem = [string]$Disk.Error }
            [pscustomobject][ordered]@{
                Path = [string]$Disk.Path
                Format = Get-ControlText $Disk.VhdFormat
                Type = Get-ControlText $Disk.VhdType
                SizeBytes = $(if ($null -ne $Disk.Size) { [long]$Disk.Size } else { $null })
                FileSizeBytes = $(if ($null -ne $Disk.FileSize) { [long]$Disk.FileSize } else { $null })
                ParentPath = $(if ([string]::IsNullOrWhiteSpace([string]$Disk.ParentPath)) { $null } else { [string]$Disk.ParentPath })
                Error = $problem
            }
        }

        # Every virtual disk file a VM other than $ExceptVm uses: attached as a current or checkpoint drive,
        # or an ancestor (differencing parent) of such a file. Map: path key -> list of references.
        function Get-ControlDiskReferences($ExceptVm) {
            $map = @{}
            $unreadable = @()
            $machines = @(Hyper-V\Get-VM -ErrorAction SilentlyContinue -ErrorVariable unreadable)
            foreach ($other in $machines) {
                if ($null -ne $ExceptVm -and [string]$other.Id -eq [string]$ExceptVm.Id) { continue }
                $drives = @(Get-VMHardDiskDrive -VM $other)
                foreach ($checkpoint in @(Get-VMSnapshot -VM $other)) { $drives += @(Get-VMHardDiskDrive -VMSnapshot $checkpoint) }
                foreach ($drive in $drives) {
                    $leaf = [string]$drive.Path
                    if ([string]::IsNullOrWhiteSpace($leaf)) { continue }
                    $links = @(Get-ControlVhdChain $leaf)
                    for ($i = 0; $i -lt $links.Count; $i++) {
                        $key = Get-ControlPathKey ([string]$links[$i].Path)
                        if (-not $map.ContainsKey($key)) { $map[$key] = New-Object System.Collections.ArrayList }
                        [void]$map[$key].Add([pscustomobject]@{ VmId = [string]$other.Id; VmName = [string]$other.Name; Attached = ($i -eq 0); Via = $leaf })
                    }
                }
            }
            return [pscustomobject]@{ Map = $map; Unreadable = @($unreadable).Count }
        }

        function Format-ControlReference($Reference) {
            if ($Reference.Attached) { return "attached to VM '" + $Reference.VmName + "' (" + $Reference.VmId + ")" }
            return 'differencing parent of ' + $Reference.Via + " (VM '" + $Reference.VmName + "')"
        }

        # Why a file is in use according to $References, an attachment before a parent link; $null if unused.
        function Get-ControlReferenceReason($References, [string]$Key) {
            if (-not $References.Map.ContainsKey($Key)) { return $null }
            $list = @($References.Map[$Key])
            $attached = @($list | Where-Object { $_.Attached })
            if ($attached.Count -gt 0) { return Format-ControlReference $attached[0] }
            return Format-ControlReference $list[0]
        }

        # Virtual disks in the given directories, other than $Exclude, whose differencing chain passes through
        # one of the given files, whether or not a VM attaches them. Map: parent key -> child file. Best effort:
        # children elsewhere on the host are not discovered.
        function Find-ControlUnattachedChildren([string[]]$Paths, [string[]]$Directories, [string[]]$Exclude = @()) {
            $targets = @{}
            $skip = @{}
            foreach ($path in $Paths) { if (-not [string]::IsNullOrWhiteSpace($path)) { $targets[(Get-ControlPathKey $path)] = $true; $skip[(Get-ControlPathKey $path)] = $true } }
            foreach ($path in $Exclude) { if (-not [string]::IsNullOrWhiteSpace($path)) { $skip[(Get-ControlPathKey $path)] = $true } }
            $found = @{}
            $scanned = @{}
            foreach ($directory in $Directories) {
                if ([string]::IsNullOrWhiteSpace($directory)) { continue }
                $directoryKey = Get-ControlPathKey $directory
                if ($scanned.ContainsKey($directoryKey) -or -not [System.IO.Directory]::Exists($directory)) { continue }
                $scanned[$directoryKey] = $true
                foreach ($file in [System.IO.Directory]::GetFiles($directory)) {
                    if ($file -notmatch '\.(vhdx?|avhdx?)$' -or $skip.ContainsKey((Get-ControlPathKey $file))) { continue }
                    $links = @(Get-ControlVhdChain $file)
                    for ($i = 1; $i -lt $links.Count; $i++) {
                        $key = Get-ControlPathKey ([string]$links[$i].Path)
                        if ($targets.ContainsKey($key) -and -not $found.ContainsKey($key)) { $found[$key] = $file }
                    }
                }
            }
            return $found
        }

        function Get-ControlFreeSpace([string]$Path) {
            try {
                $root = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($Path))
                $drive = New-Object System.IO.DriveInfo $root
                if (-not $drive.IsReady) { return $null }
                return [pscustomobject][ordered]@{ path = $Path; root = $root; freeBytes = [long]$drive.AvailableFreeSpace; totalBytes = [long]$drive.TotalSize }
            } catch { return $null }
        }

        # Refuses before any change when the volume holding $Path has less than $Bytes free. Paths without
        # a readable local volume (shares) are not checked.
        function Assert-ControlFreeSpace([string]$Path, [long]$Bytes, [string]$What) {
            $space = Get-ControlFreeSpace $Path
            if ($null -eq $space -or $space.freeBytes -ge $Bytes) { return }
            Stop-Control 'InvalidArgument' ($What + ' needs ' + [math]::Round($Bytes / 1GB, 1) + ' GB but ' + $space.root + ' has ' + [math]::Round($space.freeBytes / 1GB, 1) + ' GB free. Nothing was changed.')
        }

        function Set-ControlReadOnly([string]$Path, [bool]$ReadOnly) {
            $attributes = [System.IO.File]::GetAttributes($Path)
            if ($ReadOnly) { $attributes = $attributes -bor [System.IO.FileAttributes]::ReadOnly }
            else { $attributes = $attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly) }
            [System.IO.File]::SetAttributes($Path, [System.IO.FileAttributes]$attributes)
        }

        # Export-VM fails with 'Access is denied' (0x80070005) when any disk in a VM's chain, typically a
        # differencing parent the plugin marked read-only, carries the read-only attribute (observed on
        # Windows 11 26200). Clear it for the duration of an export and restore it afterwards.
        function Suspend-ControlChainReadOnly($Vm) {
            $cleared = New-Object System.Collections.ArrayList
            $drives = @(Get-VMHardDiskDrive -VM $Vm)
            foreach ($checkpoint in @(Get-VMSnapshot -VM $Vm)) { $drives += @(Get-VMHardDiskDrive -VMSnapshot $checkpoint) }
            foreach ($drive in $drives) {
                if ([string]::IsNullOrWhiteSpace([string]$drive.Path)) { continue }
                foreach ($link in @(Get-ControlVhdChain ([string]$drive.Path))) {
                    $path = [string]$link.Path
                    if (-not [System.IO.File]::Exists($path) -or $cleared.Contains($path)) { continue }
                    if (([System.IO.File]::GetAttributes($path) -band [System.IO.FileAttributes]::ReadOnly) -ne 0) { Set-ControlReadOnly $path $false; [void]$cleared.Add($path) }
                }
            }
            return ,$cleared.ToArray()
        }

        function Resume-ControlChainReadOnly([string[]]$Paths) {
            foreach ($path in @($Paths)) { try { if ([System.IO.File]::Exists($path)) { Set-ControlReadOnly $path $true } } catch { } }
        }

        # A VM without a key protector returns only a short empty marker from Get-VMKeyProtector.
        function Test-ControlKeyProtector($Vm) {
            try { $protector = Get-VMKeyProtector -VM $Vm } catch { return $null }
            return (@($protector).Count -gt 16)
        }

        function ConvertTo-ControlVm($Vm) {
            $memory = $null; $processor = $null; $services = [ordered]@{}
            try { $memory = Get-VMMemory -VM $Vm } catch { }
            try { $processor = Get-VMProcessor -VM $Vm } catch { }
            try { foreach ($service in @(Get-VMIntegrationService -VM $Vm)) { $services[[string]$service.Name] = [bool]$service.Enabled } } catch { }
            [pscustomobject][ordered]@{
                Id = [string]$Vm.Id
                Name = [string]$Vm.Name
                State = [string]$Vm.State
                Generation = [int]$Vm.Generation
                Version = Get-ControlText $Vm.Version
                ProcessorCount = [int]$Vm.ProcessorCount
                NestedVirtualization = $(if ($null -ne $processor) { [bool]$processor.ExposeVirtualizationExtensions } else { $null })
                MemoryStartup = [long]$Vm.MemoryStartup
                MemoryAssigned = [long]$Vm.MemoryAssigned
                DynamicMemoryEnabled = [bool]$Vm.DynamicMemoryEnabled
                MemoryMinimum = [long]$Vm.MemoryMinimum
                MemoryMaximum = [long]$Vm.MemoryMaximum
                MemoryBufferPercent = $(if ($null -ne $memory) { [int]$memory.Buffer } else { $null })
                CheckpointType = Get-ControlText $Vm.CheckpointType
                AutomaticCheckpointsEnabled = [bool]$Vm.AutomaticCheckpointsEnabled
                AutomaticStartAction = Get-ControlText $Vm.AutomaticStartAction
                AutomaticStartDelaySeconds = [int]$Vm.AutomaticStartDelay
                AutomaticStopAction = Get-ControlText $Vm.AutomaticStopAction
                EnhancedSessionTransportType = Get-ControlText $Vm.EnhancedSessionTransportType
                Notes = [string]$Vm.Notes
                IntegrationServices = $services
                Path = [string]$Vm.Path
            }
        }

        function ConvertTo-ControlAdapter($Adapter) {
            $vlan = $Adapter.VlanSetting
            [pscustomobject][ordered]@{
                Id = [string]$Adapter.Id
                Name = [string]$Adapter.Name
                SwitchName = Get-ControlText $Adapter.SwitchName
                Connected = [bool]$Adapter.Connected
                MacAddress = [string]$Adapter.MacAddress
                DynamicMacAddressEnabled = [bool]$Adapter.DynamicMacAddressEnabled
                MacAddressSpoofing = Get-ControlText $Adapter.MacAddressSpoofing
                VlanMode = $(if ($null -ne $vlan) { Get-ControlText $vlan.OperationMode } else { $null })
                VlanId = $(if ($null -ne $vlan) { [int]$vlan.AccessVlanId } else { $null })
                IPAddresses = @($Adapter.IPAddresses | Where-Object { $_ } | ForEach-Object { [string]$_ })
                Status = @($Adapter.Status | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
            }
        }

        function ConvertTo-ControlDrive($Drive) {
            $chain = @()
            if (-not [string]::IsNullOrWhiteSpace([string]$Drive.Path)) { $chain = @(Get-ControlVhdChain ([string]$Drive.Path) | ForEach-Object { ConvertTo-ControlVhdLink $_ }) }
            [pscustomobject][ordered]@{
                Path = [string]$Drive.Path
                ControllerType = Get-ControlText $Drive.ControllerType
                ControllerNumber = [int]$Drive.ControllerNumber
                ControllerLocation = [int]$Drive.ControllerLocation
                Chain = $chain
            }
        }

        function ConvertTo-ControlBootOrder($Firmware) {
            @($Firmware.BootOrder | Where-Object { $null -ne $_ } | ForEach-Object {
                $device = $_.Device
                $target = $null
                if ($null -ne $device) {
                    if ($null -ne $device.PSObject.Properties['Path'] -and -not [string]::IsNullOrWhiteSpace([string]$device.Path)) { $target = [string]$device.Path }
                    else { $target = [string]$device.Name }
                } elseif ($null -ne $_.PSObject.Properties['FirmwarePath']) { $target = [string]$_.FirmwarePath }
                [pscustomobject][ordered]@{ BootType = Get-ControlText $_.BootType; Description = [string]$_.Description; Device = $target }
            })
        }

        function ConvertTo-ControlFirmware($Vm) {
            if ([int]$Vm.Generation -ne 2) { return $null }
            $firmware = Get-VMFirmware -VM $Vm
            [pscustomobject][ordered]@{
                SecureBoot = Get-ControlText $firmware.SecureBoot
                SecureBootTemplate = Get-ControlText $firmware.SecureBootTemplate
                PreferredNetworkBootProtocol = Get-ControlText $firmware.PreferredNetworkBootProtocol
                BootOrder = @(ConvertTo-ControlBootOrder $firmware)
            }
        }

        function ConvertTo-ControlSecurity($Vm) {
            try { $security = Get-VMSecurity -VM $Vm } catch { return $null }
            [pscustomobject][ordered]@{
                TpmEnabled = [bool]$security.TpmEnabled
                KeyProtector = Test-ControlKeyProtector $Vm
                Shielded = [bool]$security.Shielded
                KsdEnabled = [bool]$security.KsdEnabled
                EncryptStateAndVmMigrationTraffic = [bool]$security.EncryptStateAndVmMigrationTraffic
                VirtualizationBasedSecurityOptOut = [bool]$security.VirtualizationBasedSecurityOptOut
            }
        }

        # Refuses before any change, listing every requested setting the VM's state does not allow. Each
        # rule: @{ setting; label; states; when }, where 'when' must also hold while the VM is Running.
        function Assert-ControlSettings($Vm, [object[]]$Rules) {
            $state = [string]$Vm.State
            if ($state -eq 'FastSaved') { $state = 'Saved' }
            $blocked = New-Object System.Collections.ArrayList
            foreach ($rule in $Rules) {
                $allowed = @($rule.states) -contains $state
                if ($allowed -and $state -eq 'Running' -and $null -ne $rule.when) { $allowed = [bool](& $rule.when) }
                if (-not $allowed) { [void]$blocked.Add([string]$rule.setting + ' (' + [string]$rule.label + ')') }
            }
            if ($blocked.Count -eq 0) { return }
            $list = $blocked -join '; '
            $prefix = "VM '" + [string]$Vm.Name + "' is " + [string]$Vm.State + '. '
            if ($state -eq 'Running') { $text = 'These settings need it Off: ' + $list + '.' }
            elseif ($state -eq 'Saved') { $text = 'A saved VM accepts only rename, removal, export and checkpoint operations. These settings need it Off: ' + $list + '.' }
            else { $text = 'Resume it or wait for Off (or Running, for live changes) before changing: ' + $list + '.' }
            Stop-Control 'InvalidVmState' ($prefix + $text + ' Nothing was changed.')
        }

        # Hyper-V merges checkpoint files asynchronously after Remove-VMSnapshot returns. Waits until the VM
        # has no checkpoints, reports no merge in progress and attaches no .avhdx file.
        function Wait-ControlMerge($Vm, [int]$TimeoutSeconds) {
            $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
            while ($true) {
                $current = Hyper-V\Get-VM -Id $Vm.Id -ErrorAction Stop
                $merging = @($current.OperationalStatus | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ }) -contains 'MergingDisks'
                $left = @(Get-VMSnapshot -VM $current).Count
                $differencing = @(Get-VMHardDiskDrive -VM $current | Where-Object { [string]$_.Path -match '\.avhdx?$' }).Count
                if (-not $merging -and $left -eq 0 -and $differencing -eq 0) { return $current }
                if ([DateTime]::UtcNow -gt $deadline) {
                    Stop-Control 'HyperVOperationFailed' ("Checkpoint merge for VM '" + $current.Name + "' did not finish within " + $TimeoutSeconds + ' s (checkpoints left: ' + $left + ', merging: ' + $merging + '). The checkpoints were deleted; the VM was not removed and no file was deleted. Retry later.')
                }
                Start-Sleep -Seconds 2
            }
        }

        # What removing $Vm does to its disk files. Own files: each drive's file and, through .avhdx
        # checkpoint files, the base disk they merge into; a linked clone's parent is never own. Own files
        # another VM uses, or that are the differencing parent of a disk beside them or in the host disk
        # folder, are kept.
        function Get-ControlRemovalPlan($Vm, [bool]$DeleteDisks) {
            $own = [ordered]@{}
            $merge = [ordered]@{}
            $checkpoints = @(Get-VMSnapshot -VM $Vm)
            $drives = @(Get-VMHardDiskDrive -VM $Vm)
            foreach ($checkpoint in $checkpoints) { $drives += @(Get-VMHardDiskDrive -VMSnapshot $checkpoint) }
            $parents = [ordered]@{}
            foreach ($drive in $drives) {
                if ([string]::IsNullOrWhiteSpace([string]$drive.Path)) { continue }
                $base = $false
                foreach ($link in @(Get-ControlVhdChain ([string]$drive.Path))) {
                    $path = [string]$link.Path
                    $key = Get-ControlPathKey $path
                    if ($base) { if (-not $parents.Contains($key)) { $parents[$key] = $path }; continue }
                    if ($path -match '\.avhdx?$') { $merge[$key] = $path; continue }
                    $own[$key] = $path
                    $base = $true
                }
            }
            $references = Get-ControlDiskReferences $Vm
            $directories = @($own.Values | ForEach-Object { [System.IO.Path]::GetDirectoryName($_) }) + @([string](Get-VMHost).VirtualHardDiskPath)
            # The VM's own checkpoint files sit beside its base disk and merge into it; they are not children.
            $children = Find-ControlUnattachedChildren @($own.Values) $directories @($merge.Values)
            $delete = New-Object System.Collections.ArrayList
            $keep = New-Object System.Collections.ArrayList
            foreach ($key in @($own.Keys)) {
                $path = $own[$key]
                $reason = Get-ControlReferenceReason $references $key
                if ($null -eq $reason -and $children.ContainsKey($key)) { $reason = 'differencing parent of ' + $children[$key] }
                if ($null -eq $reason -and $references.Unreadable -gt 0) { $reason = 'Hyper-V could not read ' + $references.Unreadable + ' VM(s), so their use of this file cannot be ruled out' }
                if ($null -eq $reason -and -not $DeleteDisks) { $reason = 'deleteDisks is false' }
                if ($null -eq $reason) { [void]$delete.Add($path) } else { [void]$keep.Add([pscustomobject][ordered]@{ path = $path; reason = $reason }) }
            }
            # Parents beyond the VM's own base disk (a linked clone's template, or the parent copied in by a full
            # clone) are reported, never deleted here: hyperv_vhd delete removes one once no disk uses it.
            foreach ($key in @($parents.Keys)) {
                if ($own.Contains($key)) { continue }
                [void]$keep.Add([pscustomobject][ordered]@{ path = $parents[$key]; reason = 'differencing parent of this VM''s disk, not owned by the VM; delete it with hyperv_vhd once no disk uses it' })
            }
            foreach ($dvd in @(Get-VMDvdDrive -VM $Vm | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.Path) })) {
                [void]$keep.Add([pscustomobject][ordered]@{ path = [string]$dvd.Path; reason = 'ISO media is never deleted' })
            }
            [pscustomobject][ordered]@{ checkpoints = $checkpoints.Count; merge = @($merge.Values); delete = @($delete); keep = @($keep) }
        }

        # Deletes a VM's own configuration folder (New-VM -Path <root> creates <root>\<name>) when it holds
        # no file at all. Volume roots and the given protected folders are never deleted.
        function Remove-ControlVmDirectory([string]$Path, [string]$Name, [string[]]$Protected) {
            try {
                if ([string]::IsNullOrWhiteSpace($Path) -or -not [System.IO.Directory]::Exists($Path)) { return $null }
                if ([System.IO.Path]::GetFileName($Path.TrimEnd('\')) -ne $Name) { return $null }
                $key = Get-ControlPathKey $Path
                if ($key -eq (Get-ControlPathKey ([System.IO.Path]::GetPathRoot($Path)))) { return $null }
                foreach ($protectedPath in $Protected) { if ($key -eq (Get-ControlPathKey $protectedPath)) { return $null } }
                if (@([System.IO.Directory]::GetFiles($Path, '*', [System.IO.SearchOption]::AllDirectories)).Count -gt 0) { return $null }
                [System.IO.Directory]::Delete($Path, $true)
                return $Path
            } catch { return $null }
        }

        # The networking modules NAT switches need; a test module standing in for Hyper-V provides them.
        function Import-ControlNetModules {
            if (-not [string]::IsNullOrWhiteSpace($env:HYPERV_CONTROL_MODULE_PATH)) { return }
            foreach ($module in 'NetAdapter', 'NetTCPIP', 'NetNat') {
                Import-Module -Name ([System.IO.Path]::Combine($systemModuleRoot, $module, $module + '.psd1')) -ErrorAction Stop
            }
        }

        function ConvertTo-ControlPrefix([string]$Prefix) {
            $parts = $Prefix.Split('/')
            $bytes = [System.Net.IPAddress]::Parse($parts[0]).GetAddressBytes()
            [Array]::Reverse($bytes)
            $address = [BitConverter]::ToUInt32($bytes, 0)
            $length = [int]$parts[1]
            $mask = if ($length -eq 0) { [uint32]0 } else { [uint32]([math]::Pow(2, 32) - [math]::Pow(2, 32 - $length)) }
            return @{ network = ($address -band $mask); mask = $mask }
        }

        function Test-ControlPrefixOverlap([string]$First, [string]$Second) {
            $a = ConvertTo-ControlPrefix $First
            $b = ConvertTo-ControlPrefix $Second
            $mask = $a.mask -band $b.mask
            return (($a.network -band $mask) -eq ($b.network -band $mask))
        }
        """;
}
