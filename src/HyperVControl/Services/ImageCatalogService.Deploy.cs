using System.Text.Json;

namespace HyperVControl.Services;

/// <summary>Deploying a VM from a library entry with the lab defaults.</summary>
internal sealed partial class ImageCatalogService
{
    private const string VmProjection = "($vm | Select-Object Id,Name,@{Name='State';Expression={[string]$_.State}},Generation,ProcessorCount,MemoryStartup,DynamicMemoryEnabled,"
        + "@{Name='CheckpointType';Expression={[string]$_.CheckpointType}},AutomaticCheckpointsEnabled,"
        + "@{Name='EnhancedSessionTransportType';Expression={[string]$_.EnhancedSessionTransportType}},Path,Notes)";

    /// <summary>
    /// Creates a VM from a library entry: a differencing child of the entry (linked, the default) or a full copy under
    /// the VM's folder, the VM through <see cref="VmLifecycleService.CreateAsync"/>, then the lab defaults in one script:
    /// static memory, automatic checkpoints off, Standard checkpoints, Secure Boot per OS family (off when the image
    /// says so), HvSocket enhanced session for Linux, and a vTPM for Windows (after the template, which the vTPM locks).
    /// Any failure removes the half-created VM and its new disk.
    /// </summary>
    public async Task<object> DeployAsync(ImageDeployRequest request, VmLifecycleService lifecycle, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ImageName)) throw new ArgumentException("imageName required: a library entry name from hyperv_image list.");
        var vmName = request.VmName?.Trim();
        if (string.IsNullOrEmpty(vmName) || vmName.Length > 100) throw new ArgumentException("vmName required (1-100 characters).");
        if (request.MemoryMb is < 32 or > 1048576 || request.Processors is < 1 or > 2048) throw new ArgumentException("Invalid memoryMb or processors.");
        var store = await StoreAsync(ct);
        var entry = (await store.LibraryAsync(ct)).Find(request.ImageName.Trim())
            ?? throw new HyperVOperationException($"No library entry named '{request.ImageName}'. Fetch or register it first; hyperv_image list shows the library.", "NotFound");
        if (entry.Format != "vhdx")
            throw new ArgumentException($"'{entry.Name}' is an ISO (installation media), not a bootable disk. Create a VM with a new disk and attach the ISO instead.");
        if (!File.Exists(entry.Path)) throw new HyperVOperationException($"The file of '{entry.Name}' is missing: {entry.Path}.", "NotFound");
        var vmRoot = request.VmPath is null ? (await HostPathsAsync(ct)).VirtualMachinePath : ControlService.FullPath(request.VmPath);
        var folder = Path.Combine(vmRoot, ImageFiles.SafeDirectoryName(vmName));
        var directory = Path.Combine(folder, "Virtual Hard Disks");
        var disk = Path.Combine(directory, ImageFiles.SafeDirectoryName(vmName) + ".vhdx");
        if (File.Exists(disk)) throw new HyperVOperationException($"{disk} already exists; choose another vmName or vmPath.", "AlreadyExists");
        var warnings = new List<string>(entry.Warnings ?? []);
        var createdFolder = !Directory.Exists(folder);
        Directory.CreateDirectory(directory);
        var ownsDisk = false;
        string? vmId = null;
        try
        {
            // 1. One script: refuse a duplicate VM name, resolve the switch, and create the differencing child.
            var prepare = new HyperVScript()
                .Line("$name = " + PowerShellRunner.Literal(vmName))
                .Line("if (@(Get-VM | Where-Object { $_.Name -eq $name }).Count -gt 0) { Stop-Control 'InvalidArgument' (\"A VM named '\" + $name + \"' already exists. Choose another vmName.\") }");
            if (request.SwitchName is { } requested)
                prepare.Line("$switchName = " + PowerShellRunner.Literal(requested))
                    .Line("if (@(Get-VMSwitch | Where-Object { $_.Name -eq $switchName }).Count -eq 0) { Stop-Control 'NotFound' (\"Switch '\" + $switchName + \"' not found. Call hyperv_network list.\") }");
            else
                prepare.Line("$switchName = $null")
                    .Line("try { $default = Get-VMSwitch -Id " + PsValue.Of(Guid.Parse(DefaultSwitchId)).Text + " -ErrorAction Stop; if ($default) { $switchName = [string]@($default)[0].Name } } catch { }");
            if (request.Linked)
                prepare.Cmd("$child = New-VHD", ("Path", PsValue.Of(disk)), ("ParentPath", PsValue.Of(entry.Path)), ("Differencing", PsValue.Of(true)));
            ownsDisk = request.Linked;
            var prepared = await Runner.InvokeAsync(prepare.Returns("@{ switchName = $switchName }"), ct, 300);
            var switchName = prepared.TryGetProperty("switchName", out var chosen) && chosen.ValueKind == JsonValueKind.String ? chosen.GetString() : null;
            if (switchName is null) warnings.Add("No switchName was given and this host has no Default Switch, so the VM's network adapter is not connected.");
            if (request.Linked) entry = await ProtectParentAsync(store, entry, ct);
            else
            {
                ImageFiles.EnsureSpace(directory, new FileInfo(entry.Path).Length, "copying " + entry.Name);
                ownsDisk = true;
                await ImageFiles.CopyAsync(entry.Path, disk, null, ct);
            }

            // 2. The VM itself; this is the only call into the lifecycle service.
            var created = await lifecycle.CreateAsync(vmName, vmRoot, disk, 2, request.MemoryMb, request.Processors, 0, switchName, ct);
            vmId = created.GetProperty("Id").GetString() ?? throw new HyperVOperationException("New-VM returned no VM ID.", "InvalidResponse");

            // 3. Lab defaults, then the optional start.
            var configured = await Runner.InvokeAsync(LabDefaults(vmId, entry, request, warnings), ct, 300);
            if (configured.TryGetProperty("startError", out var startError) && startError.ValueKind == JsonValueKind.String)
                warnings.Add("The VM was created and configured but did not start: " + startError.GetString());
            var deployment = new ImageDeployment(vmName, vmId, disk, request.Linked, Clock());
            await store.UpdateLibraryAsync(index => index.Find(entry.Name) is { } current
                ? (Replace(index, current, current with { Deployments = [.. current.Deployments ?? [], deployment] }), true)
                : (index, false), ct);
            return new
            {
                vm = configured.GetProperty("vm"),
                disk = new { path = disk, type = request.Linked ? "differencing" : "copy", parentPath = request.Linked ? entry.Path : null },
                parent = new { entry.Name, entry.Path, entry.Kind, entry.OsFamily, entry.ReadOnly },
                switchName,
                firmware = configured.TryGetProperty("firmware", out var firmware) ? firmware : default,
                security = configured.TryGetProperty("security", out var security) ? security : default,
                started = request.Start && !(startError.ValueKind == JsonValueKind.String),
                warnings
            };
        }
        catch
        {
            await RollbackAsync(vmId, ownsDisk ? disk : null, createdFolder ? folder : directory);
            throw;
        }
    }

    private static HyperVScript LabDefaults(string vmId, ImageLibraryEntry entry, ImageDeployRequest request, List<string> warnings)
    {
        var vm = PsValue.Raw("$vm");
        var windows = entry.OsFamily == "windows";
        var linux = entry.OsFamily == "linux";
        var transport = entry.Config.EnhancedSessionTransportType ?? (linux ? "HvSocket" : null);
        var script = new HyperVScript().Vm(vmId).RequireOff()
            .Cmd("Set-VM", ("VM", vm), ("StaticMemory", PsValue.Of(true)), ("AutomaticCheckpointsEnabled", PsValue.Of(false)),
                ("CheckpointType", PsValue.Token("Standard", "Standard")),
                ("EnhancedSessionTransportType", transport is null ? null : PsValue.Token(transport, "HvSocket", "VMBus")),
                ("Notes", request.Notes is null ? null : PsValue.Of(request.Notes)));
        // Secure Boot follows the image (off when it says so), else the template that matches the OS family.
        var template = entry.Config.SecureBoot == false ? null
            : windows ? "MicrosoftWindows"
            : linux || entry.Config.SecureBoot == true ? "MicrosoftUEFICertificateAuthority"
            : null;
        if (template is null)
        {
            script.Cmd("Set-VMFirmware", ("VM", vm), ("EnableSecureBoot", PsValue.Token("Off", "On", "Off")));
            if (entry.Config.SecureBoot is null)
                warnings.Add("Secure Boot is off because the image's OS family is 'other'; enable it with hyperv_firmware if the guest supports it.");
        }
        else
            script.Cmd("Set-VMFirmware", ("VM", vm), ("EnableSecureBoot", PsValue.Token("On", "On", "Off")),
                ("SecureBootTemplate", PsValue.Token(template, "MicrosoftWindows", "MicrosoftUEFICertificateAuthority")));
        // The vTPM comes after the template: initializing it locks the Secure Boot template.
        if (windows) script.Cmd("Set-VMKeyProtector", ("VM", vm), ("NewLocalKeyProtector", PsValue.Of(true))).Cmd("Enable-VMTPM", ("VM", vm));
        script.Line("$startError = $null");
        if (request.Start) script.Line("try { Start-VM -VM $vm } catch { $startError = [string]$_.Exception.Message }");
        return script.Line("$vm = Get-VM -Id $vm.Id -ErrorAction Stop").Returns(
            "@{ vm = " + VmProjection
            + "; firmware = (Get-VMFirmware -VM $vm | Select-Object @{Name='SecureBoot';Expression={[string]$_.SecureBoot}},SecureBootTemplate)"
            + "; security = (Get-VMSecurity -VM $vm | Select-Object TpmEnabled); startError = $startError }");
    }

    private static async Task<ImageLibraryEntry> ProtectParentAsync(ImageLibraryStore store, ImageLibraryEntry entry, CancellationToken ct) =>
        await store.UpdateLibraryAsync(index =>
        {
            var current = index.Find(entry.Name) ?? entry;
            MakeReadOnly(current.Path);
            if (current.ReadOnly || !index.Entries.Contains(current)) return (index, current with { ReadOnly = true });
            var updated = current with { ReadOnly = true };
            return (Replace(index, current, updated), updated);
        }, ct);

    /// <summary>Best effort: removes a half-created VM, the disk this deploy created and the folders it left empty.</summary>
    private async Task RollbackAsync(string? vmId, string? disk, string folder)
    {
        if (vmId is not null && Guid.TryParse(vmId, out var id))
        {
            try
            {
                await Runner.InvokeAsync(new HyperVScript()
                    .Line("$vm = $null; try { $vm = Get-VM -Id " + PsValue.Of(id).Text + " -ErrorAction Stop } catch { }")
                    .Line("if ($vm) { if ([string]$vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force }; Remove-VM -VM $vm -Force }")
                    .Returns("$true"), CancellationToken.None, 120);
            }
            catch (Exception) { /* the original failure is the one reported */ }
        }
        ImageFiles.TryDelete(disk);
        DeleteEmptyFolders(folder);
    }

    private static void DeleteEmptyFolders(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var child in Directory.GetDirectories(folder)) DeleteEmptyFolders(child);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
