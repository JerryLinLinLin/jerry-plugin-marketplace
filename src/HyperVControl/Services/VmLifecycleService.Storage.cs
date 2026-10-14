using System.Text.Json;

namespace HyperVControl.Services;

/// <summary>Standalone virtual disk files and VM clones.</summary>
internal sealed partial class VmLifecycleService
{
    private static readonly string[] DiskTypes = ["dynamic", "fixed", "differencing"];

    /// <summary>Format, sizes, parent chain, attaching VMs and children of <c>$__path</c>.</summary>
    private const string VhdDetails = """
        $__chain = @(Get-ControlVhdChain $__path)
        $__disk = $__chain[0]
        $__uses = @($__references.Map[(Get-ControlPathKey $__path)] | Where-Object { $null -ne $_ })
        $__children = Find-ControlUnattachedChildren @($__path) @([System.IO.Path]::GetDirectoryName($__path), [string](Get-VMHost).VirtualHardDiskPath)
        $__details = [ordered]@{
            path = $__path
            format = Get-ControlText $__disk.VhdFormat
            type = Get-ControlText $__disk.VhdType
            sizeBytes = $(if ($null -ne $__disk.Size) { [long]$__disk.Size } else { $null })
            fileSizeBytes = $(if ($null -ne $__disk.FileSize) { [long]$__disk.FileSize } else { $null })
            minimumSizeBytes = $(if ($null -ne $__disk.MinimumSize) { [long]$__disk.MinimumSize } else { $null })
            parentPath = $(if ([string]::IsNullOrWhiteSpace([string]$__disk.ParentPath)) { $null } else { [string]$__disk.ParentPath })
            diskIdentifier = Get-ControlText $__disk.DiskIdentifier
            mountedOnHost = [bool]$__disk.Attached
            readOnly = (([System.IO.File]::GetAttributes($__path) -band [System.IO.FileAttributes]::ReadOnly) -ne 0)
            error = $(if ($null -ne $__disk.PSObject.Properties['Error']) { [string]$__disk.Error } else { $null })
            chain = @($__chain | ForEach-Object { ConvertTo-ControlVhdLink $_ })
            attachedTo = @($__uses | Where-Object { $_.Attached } | ForEach-Object { [ordered]@{ vmId = $_.VmId; vmName = $_.VmName } })
            parentOf = @(@($__uses | Where-Object { -not $_.Attached } | ForEach-Object { [ordered]@{ child = $_.Via; vmId = $_.VmId; vmName = $_.VmName } }) + @($__children.Values | ForEach-Object { [ordered]@{ child = [string]$_; vmId = $null; vmName = $null } }))
        }
        """;

    /// <summary>create (dynamic | fixed | differencing), inspect, or delete one virtual disk file.</summary>
    public Task<JsonElement> VhdAsync(string action, string path, string type, long? sizeGb, string? parentPath, bool dryRun, CancellationToken ct)
    {
        ControlService.FullPath(path);
        var s = Script().Line("$__path = " + L(path)).Line("$__warnings = New-Object System.Collections.ArrayList");
        switch (action)
        {
            case "create":
            {
                var kind = Choice(type, DiskTypes, "type");
                if (Extension(path) is not (".vhdx" or ".vhd")) throw new ArgumentException("path must end in .vhdx (or .vhd).");
                if (kind == "differencing")
                {
                    if (parentPath is null || sizeGb is not null) throw new ArgumentException("type=differencing needs parentPath and no sizeGb; the child has its parent's size.");
                    if (Extension(ControlService.FullPath(parentPath)) != Extension(path)) throw new ArgumentException("A differencing disk must use its parent's format (.vhdx or .vhd).");
                }
                else
                {
                    if (parentPath is not null) throw new ArgumentException("parentPath applies only to type=differencing.");
                    if (sizeGb is not > 0 || sizeGb > (Extension(path) == ".vhd" ? 2040 : 65536)) throw new ArgumentException("sizeGb must be 1-65536 for .vhdx (1-2040 for .vhd).");
                }
                s.Line("if ([System.IO.File]::Exists($__path) -or [System.IO.Directory]::Exists($__path)) { Stop-Control 'InvalidArgument' ('Something already exists at ' + $__path + '; hyperv_vhd create never overwrites. Nothing was changed.') }");
                if (kind == "differencing")
                    s.Line("$__parent = " + L(parentPath!))
                        .Line("if (-not [System.IO.File]::Exists($__parent)) { Stop-Control 'NotFound' ('Parent disk not found: ' + $__parent) }")
                        .Line("$__attached = @((Get-ControlDiskReferences $null).Map[(Get-ControlPathKey $__parent)] | Where-Object { $null -ne $_ -and $_.Attached })")
                        .Line("if ($__attached.Count -gt 0) { Stop-Control 'InvalidArgument' ('Parent ' + $__parent + ' is ' + (Format-ControlReference $__attached[0]) + '; a differencing parent must never change. Nothing was changed.') }")
                        .Line("Assert-ControlFreeSpace $__path ([long](Get-VHD -Path $__parent).Size) ('A differencing disk over ' + $__parent)");
                else s.Line($"Assert-ControlFreeSpace $__path ([long]{sizeGb}*1GB) 'A {sizeGb} GB disk'");
                s.Line("[void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($__path))");
                if (kind == "differencing")
                    s.Cmd("$null = New-VHD", ("Path", PsValue.Raw("$__path")), ("ParentPath", PsValue.Raw("$__parent")), ("Differencing", PsValue.Of(true)))
                        .Line("Set-ControlReadOnly $__parent $true")
                        .Line("[void]$__warnings.Add('Marked the parent ' + $__parent + ' read-only: changing it would break this differencing disk.')");
                else
                    s.Cmd("$null = New-VHD", ("Path", PsValue.Raw("$__path")), ("SizeBytes", PsValue.Gigabytes(sizeGb!.Value)), (kind == "fixed" ? "Fixed" : "Dynamic", PsValue.Of(true)));
                s.Line("$__references = Get-ControlDiskReferences $null").Line(VhdDetails)
                    .Line("$__details.createdFiles = @($__path)").Line("$__details.warnings = @($__warnings)");
                return runner.InvokeAsync(s.Returns("$__details"), ct, kind == "fixed" ? 3600 : 300);
            }
            case "inspect":
                s.Line("if (-not [System.IO.File]::Exists($__path)) { Stop-Control 'NotFound' ('Virtual disk not found: ' + $__path) }")
                    .Line("$__references = Get-ControlDiskReferences $null").Line(VhdDetails);
                return runner.InvokeAsync(s.Returns("$__details"), ct, 300);
            case "delete":
                if (Extension(path) is not (".vhdx" or ".vhd" or ".avhdx" or ".avhd")) throw new ArgumentException("hyperv_vhd deletes only .vhdx, .vhd, .avhdx and .avhd files.");
                s.Line("if (-not [System.IO.File]::Exists($__path)) { Stop-Control 'NotFound' ('Virtual disk not found: ' + $__path) }")
                    .Line("$__references = Get-ControlDiskReferences $null").Line(VhdDetails)
                    .Line("""
                        $__blockers = New-Object System.Collections.ArrayList
                        foreach ($__use in $__uses) { [void]$__blockers.Add((Format-ControlReference $__use)) }
                        foreach ($__child in @($__children.Values)) { [void]$__blockers.Add('differencing parent of ' + $__child) }
                        if ($__details.mountedOnHost) { [void]$__blockers.Add('mounted on the host (Dismount-VHD it first)') }
                        if ($__references.Unreadable -gt 0) { [void]$__blockers.Add('Hyper-V could not read ' + $__references.Unreadable + ' VM(s), so their use of this file cannot be ruled out') }
                        """);
                if (dryRun)
                    return runner.InvokeAsync(s.Returns("[ordered]@{ success = $true; dryRun = $true; path = $__path; deletable = ($__blockers.Count -eq 0); blockers = @($__blockers); readOnly = $__details.readOnly; details = $__details }"), ct, 300);
                s.Line("if ($__blockers.Count -gt 0) { Stop-Control 'InvalidArgument' ('Refusing to delete ' + $__path + ': ' + ($__blockers -join '; ') + '. Nothing was changed.') }")
                    .Line("Set-ControlReadOnly $__path $false")
                    .Line("Microsoft.PowerShell.Management\\Remove-Item -LiteralPath $__path -Force -ErrorAction Stop")
                    .Line("if ([System.IO.File]::Exists($__path)) { Stop-Control 'HyperVOperationFailed' ($__path + ' still exists after deletion.') }");
                return runner.InvokeAsync(s.Returns("[ordered]@{ success = $true; dryRun = $false; deleted = @($__path); wasReadOnly = $__details.readOnly }"), ct, 300);
            default:
                throw new ArgumentException("action must be create, inspect or delete.");
        }
    }

    /// <summary>
    /// Clones an Off VM. linked: differencing disks over the source's current disks, which become
    /// read-only parents, plus a new VM with the source's settings. full: Export-VM, then Import-VM
    /// -Copy -GenerateNewId into a fresh folder (synchronous; minutes for large disks).
    /// </summary>
    public Task<JsonElement> CloneAsync(string machine, string newName, string mode, string? destination, CancellationToken ct)
    {
        VmName(newName, "newName");
        var kind = Choice(mode, ["linked", "full"], "mode");
        if (destination is not null) ControlService.FullPath(destination);
        var s = Script().Vm(machine).RequireState(VmStateRules.StateNames("clone"))
            .Line("$__warnings = New-Object System.Collections.ArrayList")
            .Line("$__created = New-Object System.Collections.ArrayList")
            .Line("$__name = " + L(newName))
            .Line("$__same = @(Hyper-V\\Get-VM | Where-Object { $_.Name -eq $__name })")
            .Line("if ($__same.Count -gt 0) { Stop-Control 'InvalidArgument' (\"A VM named '\" + $__name + \"' already exists (ID \" + $__same[0].Id + '). Choose another newName. Nothing was changed.') }")
            .Line("$__host = Get-VMHost")
            .Line("$__root = " + (destination is null ? "[string]$__host.VirtualMachinePath" : L(destination)));
        if (kind == "linked")
        {
            s.Line("$__vhdRoot = " + (destination is null ? "[string]$__host.VirtualHardDiskPath" : "[System.IO.Path]::Combine($__root, $__name, 'Virtual Hard Disks')"))
                .Line(LinkedClone);
            return runner.InvokeAsync(s.Returns("$__result"), ct, 600);
        }
        return runner.InvokeAsync(s.Line(FullClone).Returns("$__result"), ct, 7200);
    }

    private const string CloneResult = """
        $__clone = Hyper-V\Get-VM -Id $__clone.Id -ErrorAction Stop
        $__result = [ordered]@{}
        foreach ($__property in (ConvertTo-ControlVm $__clone).PSObject.Properties) { $__result[$__property.Name] = $__property.Value }
        $__result.source = [ordered]@{ id = [string]$vm.Id; name = [string]$vm.Name }
        """;

    private const string LinkedClone = """
        if (@(Get-VMSnapshot -VM $vm).Count -gt 0) { Stop-Control 'InvalidVmState' ("VM '" + $vm.Name + "' has checkpoints, so its current disks are checkpoint files that Hyper-V merges and changes. Remove its checkpoints first, or use mode=full. Nothing was changed.") }
        $__drives = @(Get-VMHardDiskDrive -VM $vm | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.Path) } | Sort-Object { [string]$_.ControllerType }, ControllerNumber, ControllerLocation)
        if ($__drives.Count -eq 0) { Stop-Control 'InvalidArgument' ("VM '" + $vm.Name + "' has no virtual disk to link to; use hyperv_vm_create instead.") }
        $__references = Get-ControlDiskReferences $vm
        $__links = @()
        for ($__i = 0; $__i -lt $__drives.Count; $__i++) {
            $__parentPath = [string]$__drives[$__i].Path
            $__suffix = ''
            if ($__i -gt 0) { $__suffix = '-' + $__i }
            $__child = [System.IO.Path]::Combine($__vhdRoot, $__name + $__suffix + [System.IO.Path]::GetExtension($__parentPath))
            if ([System.IO.File]::Exists($__child)) { Stop-Control 'InvalidArgument' ('Disk file already exists: ' + $__child + '. Choose another newName or destination. Nothing was changed.') }
            $__users = @($__references.Map[(Get-ControlPathKey $__parentPath)] | Where-Object { $null -ne $_ -and $_.Attached })
            if ($__users.Count -gt 0) { Stop-Control 'InvalidArgument' ('Source disk ' + $__parentPath + ' is also ' + (Format-ControlReference $__users[0]) + '; a linked parent must never change. Nothing was changed.') }
            $__links += [pscustomobject]@{ Drive = $__drives[$__i]; Parent = $__parentPath; Child = $__child }
        }
        $__memory = Get-VMMemory -VM $vm
        $__processor = Get-VMProcessor -VM $vm
        $__adapters = @(Get-VMNetworkAdapter -VM $vm)
        $__firmware = $null; $__security = $null
        if ([int]$vm.Generation -eq 2) { $__firmware = Get-VMFirmware -VM $vm; $__security = Get-VMSecurity -VM $vm }
        $__clone = $null
        try {
            [void][System.IO.Directory]::CreateDirectory($__vhdRoot)
            if (-not [System.IO.Directory]::Exists($__root)) { [void][System.IO.Directory]::CreateDirectory($__root) }
            foreach ($__link in $__links) { New-VHD -Path $__link.Child -ParentPath $__link.Parent -Differencing:$true | Out-Null; [void]$__created.Add($__link.Child) }
            $__clone = New-VM -Name $__name -Path $__root -Generation ([int]$vm.Generation) -MemoryStartupBytes ([long]$__memory.Startup) -VHDPath $__links[0].Child
            for ($__i = 1; $__i -lt $__links.Count; $__i++) {
                $__drive = $__links[$__i].Drive
                Add-VMHardDiskDrive -VM $__clone -ControllerType $__drive.ControllerType -ControllerNumber $__drive.ControllerNumber -ControllerLocation $__drive.ControllerLocation -Path $__links[$__i].Child
            }
            Set-VMProcessor -VM $__clone -Count ([long]$vm.ProcessorCount) -ExposeVirtualizationExtensions:([bool]$__processor.ExposeVirtualizationExtensions)
            # New-VM enables dynamic memory on current Windows hosts, so the source's memory mode is copied either way.
            if ($__memory.DynamicMemoryEnabled) { Set-VMMemory -VM $__clone -DynamicMemoryEnabled:$true -MinimumBytes ([long]$__memory.Minimum) -MaximumBytes ([long]$__memory.Maximum) -Buffer ([int]$__memory.Buffer) }
            else { Set-VMMemory -VM $__clone -DynamicMemoryEnabled:$false }
            $__settings = @{ CheckpointType = $vm.CheckpointType; AutomaticCheckpointsEnabled = [bool]$vm.AutomaticCheckpointsEnabled }
            if ($null -ne $vm.EnhancedSessionTransportType) { $__settings.EnhancedSessionTransportType = $vm.EnhancedSessionTransportType }
            Set-VM -VM $__clone @__settings
            if ($null -ne $__firmware) {
                # The template locks once the vTPM is initialized, so it is copied first.
                Set-VMFirmware -VM $__clone -SecureBootTemplate ([string]$__firmware.SecureBootTemplate)
                Set-VMFirmware -VM $__clone -EnableSecureBoot $__firmware.SecureBoot
                if ($__security.TpmEnabled) { Set-VMKeyProtector -VM $__clone -NewLocalKeyProtector:$true; Enable-VMTPM -VM $__clone }
                # New-VM puts the network adapter ahead of the disk; the clone boots from its disk first.
                Set-VMFirmware -VM $__clone -FirstBootDevice (@(Get-VMHardDiskDrive -VM $__clone) | Select-Object -First 1)
            }
            foreach ($__default in @(Get-VMNetworkAdapter -VM $__clone)) { Remove-VMNetworkAdapter -VMNetworkAdapter $__default }
            foreach ($__adapter in $__adapters) {
                $__add = @{ Name = [string]$__adapter.Name }
                if (-not [string]::IsNullOrWhiteSpace([string]$__adapter.SwitchName)) { $__add.SwitchName = [string]$__adapter.SwitchName }
                Add-VMNetworkAdapter -VM $__clone @__add
            }
            foreach ($__link in $__links) { Set-ControlReadOnly $__link.Parent $true }
        } catch {
            $__failure = $_
            if ($null -ne $__clone) { try { Remove-VM -VM $__clone -Force } catch { } }
            foreach ($__file in @($__created)) { try { if ([System.IO.File]::Exists($__file)) { [System.IO.File]::Delete($__file) } } catch { } }
            throw $__failure
        }
        [void]$__warnings.Add("Marked the source disks read-only: they are now the parents of this clone's differencing disks. VM '" + $vm.Name + "' can no longer start with them, and any change to them breaks every linked clone. Remove the clones and clear the read-only attribute before using the source again.")
        [void]$__warnings.Add('The clone runs the same guest installation as the source (same computer name and SID); generalize the source with sysprep first when both must be on one network or domain.')
        if ($null -ne $__security -and $__security.TpmEnabled) { [void]$__warnings.Add('The clone has a new virtual TPM with a local key protector; secrets the guest sealed to the source TPM (BitLocker, Windows Hello) are not available in the clone.') }
        """ + "\n" + CloneResult + "\n" + """
        $__result.mode = 'linked'
        $__result.createdFiles = @($__created)
        $__result.readOnlyParents = @($__links | ForEach-Object { $_.Parent })
        $__result.warnings = @($__warnings)
        """;

    private const string FullClone = """
        $__target = [System.IO.Path]::Combine($__root, $__name)
        if ([System.IO.File]::Exists($__target) -or ([System.IO.Directory]::Exists($__target) -and @([System.IO.Directory]::GetFileSystemEntries($__target)).Count -gt 0)) { Stop-Control 'InvalidArgument' ('Destination ' + $__target + ' already exists and is not empty. Nothing was changed.') }
        $__bytes = [long]0
        $__seen = @{}
        $__drives = @(Get-VMHardDiskDrive -VM $vm)
        foreach ($__checkpoint in @(Get-VMSnapshot -VM $vm)) { $__drives += @(Get-VMHardDiskDrive -VMSnapshot $__checkpoint) }
        foreach ($__drive in $__drives) {
            if ([string]::IsNullOrWhiteSpace([string]$__drive.Path)) { continue }
            foreach ($__link in @(Get-ControlVhdChain ([string]$__drive.Path))) {
                $__key = Get-ControlPathKey ([string]$__link.Path)
                if (-not $__seen.ContainsKey($__key) -and $null -ne $__link.FileSize) { $__seen[$__key] = $true; $__bytes += [long]$__link.FileSize }
            }
        }
        Assert-ControlFreeSpace $__root (2 * $__bytes + 1GB) ('Exporting and importing ' + [math]::Round($__bytes / 1GB, 1) + ' GB of disks')
        $__export = [System.IO.Path]::Combine($__root, '.hyperv-control-export-' + [guid]::NewGuid().ToString('N'))
        $__createdTarget = -not [System.IO.Directory]::Exists($__target)
        $__clone = $null
        $__restore = @()
        try {
            [void][System.IO.Directory]::CreateDirectory($__export)
            $__restore = Suspend-ControlChainReadOnly $vm
            Export-VM -VM $vm -Path $__export
            $__config = @(Microsoft.PowerShell.Management\Get-ChildItem -LiteralPath $__export -Recurse -Filter '*.vmcx' -File | Where-Object { $_.Extension -eq '.vmcx' -and $_.Directory.Name -eq 'Virtual Machines' }) | Select-Object -First 1
            if ($null -eq $__config) { Stop-Control 'HyperVOperationFailed' 'Export-VM produced no virtual machine configuration (.vmcx).' }
            $__clone = Import-VM -Path $__config.FullName -Copy:$true -GenerateNewId:$true -VirtualMachinePath $__target -VhdDestinationPath ([System.IO.Path]::Combine($__target, 'Virtual Hard Disks')) -SnapshotFilePath $__target -SmartPagingFilePath $__target
            Rename-VM -VM $__clone -NewName $__name
            foreach ($__adapter in @(Get-VMNetworkAdapter -VM $__clone | Where-Object { -not $_.DynamicMacAddressEnabled })) { Set-VMNetworkAdapter -VMNetworkAdapter $__adapter -DynamicMacAddress:$true }
            if (@(Get-VMSnapshot -VM $__clone).Count -eq 0) {
                foreach ($__drive in @(Get-VMHardDiskDrive -VM $__clone | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.Path) })) {
                    if ([string](Get-VHD -Path $__drive.Path).VhdType -ne 'Differencing') { Set-VHD -Path $__drive.Path -ResetDiskIdentifier:$true -Force:$true }
                }
            } else { [void]$__warnings.Add('The clone keeps the source''s checkpoints, so its disk identifiers were not reset.') }
        } catch {
            $__failure = $_
            if ($null -ne $__clone) { try { Remove-VM -VM $__clone -Force } catch { } }
            if ($__createdTarget -and [System.IO.Directory]::Exists($__target)) { try { [System.IO.Directory]::Delete($__target, $true) } catch { } }
            throw $__failure
        } finally {
            Resume-ControlChainReadOnly $__restore
            if ([System.IO.Directory]::Exists($__export)) { try { [System.IO.Directory]::Delete($__export, $true) } catch { [void]$__warnings.Add('Could not delete the temporary export ' + $__export + ': ' + $_.Exception.Message) } }
        }
        [void]$__warnings.Add('The clone is a copy of the same guest installation and vTPM (same computer name, SID and TPM identity); generalize the source with sysprep first when both must be on one network or domain.')
        """ + "\n" + CloneResult + "\n" + """
        $__result.mode = 'full'
        $__result.createdFiles = @(Get-VMHardDiskDrive -VM $__clone | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.Path) } | ForEach-Object { [string]$_.Path })
        $__result.warnings = @($__warnings)
        """;
}
