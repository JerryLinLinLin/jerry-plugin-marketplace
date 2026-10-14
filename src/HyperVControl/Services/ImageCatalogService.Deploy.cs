using System.Text.Json;

namespace HyperVControl.Services;

/// <summary>Deploying a VM from a library entry with the lab defaults.</summary>
internal sealed partial class ImageCatalogService
{

    /// <summary>
    /// Creates a VM from a library entry: a differencing child of the entry (linked, the default) or a full copy under
    /// the VM's folder, then the VM through <see cref="VmLifecycleService.CreateAsync"/> with the image's OS family and
    /// Secure Boot setting, which applies the lab defaults (static memory, automatic checkpoints off, Standard checkpoints,
    /// Secure Boot per OS family, HvSocket for Linux, a vTPM for Windows). Any failure removes the half-created VM and its new disk.
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
            // 1. A full copy is made here; a linked child is created by the lifecycle service as part of the VM.
            if (!request.Linked)
            {
                ImageFiles.EnsureSpace(directory, new FileInfo(entry.Path).Length, "copying " + entry.Name);
                ownsDisk = true;
                await ImageFiles.CopyAsync(entry.Path, disk, null, ct);
            }

            // 2. The VM, through the one create path: it refuses duplicate names and attached parents before any change,
            //    resolves the Default Switch by ID, applies the lab defaults (static memory, automatic checkpoints off,
            //    Standard checkpoints, Secure Boot per OS family, HvSocket for Linux, a vTPM for Windows) and rolls
            //    back its own half-made VM and disks on failure.
            var osFamily = entry.OsFamily is "windows" or "linux" ? entry.OsFamily : "other";
            if (osFamily == "other" && entry.Config.SecureBoot is null)
                warnings.Add("Secure Boot is off because the image's OS family is 'other'; enable it with hyperv_firmware if the guest supports it.");
            var created = await lifecycle.CreateAsync(new VmCreateRequest(vmName)
            {
                Source = request.Linked ? "linked" : "existing_vhd",
                VhdPath = disk,
                ParentPath = request.Linked ? entry.Path : null,
                Path = vmRoot,
                MemoryMb = request.MemoryMb,
                Processors = request.Processors,
                SwitchName = request.SwitchName,
                OsFamily = osFamily,
                SecureBoot = entry.Config.SecureBoot,
                Notes = request.Notes,
                Start = request.Start
            }, ct);
            vmId = created.GetProperty("Id").GetString() ?? throw new HyperVOperationException("New-VM returned no VM ID.", "InvalidResponse");
            ownsDisk = true;
            if (request.Linked) entry = await ProtectParentAsync(store, entry, ct);
            foreach (var warning in created.GetProperty("warnings").EnumerateArray())
                if (warning.GetString() is { } text) warnings.Add(text);

            // 3. Record the deployment so remove knows this entry is a live parent.
            var deployment = new ImageDeployment(vmName, vmId, disk, request.Linked, Clock());
            await store.UpdateLibraryAsync(index => index.Find(entry.Name) is { } current
                ? (Replace(index, current, current with { Deployments = [.. current.Deployments ?? [], deployment] }), true)
                : (index, false), ct);
            var state = created.TryGetProperty("State", out var vmState) && vmState.ValueKind == JsonValueKind.String ? vmState.GetString() : null;
            return new
            {
                vm = created,
                disk = new { path = disk, type = request.Linked ? "differencing" : "copy", parentPath = request.Linked ? entry.Path : null },
                parent = new { entry.Name, entry.Path, entry.Kind, entry.OsFamily, entry.ReadOnly },
                started = request.Start && string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase),
                warnings
            };
        }
        catch
        {
            await RollbackAsync(vmId, ownsDisk ? disk : null, createdFolder ? folder : directory);
            throw;
        }
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
