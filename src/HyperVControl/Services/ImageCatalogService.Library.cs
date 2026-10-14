using System.Text.Json;

namespace HyperVControl.Services;

/// <summary>Library entries: adopting local files in place and removing entries that nothing uses.</summary>
internal sealed partial class ImageCatalogService
{
    private static readonly string[] OsFamilies = ["windows", "linux", "other"];
    private static readonly string[] Kinds = ["image", "template"];

    // Walks each disk's parent chain (current disks, checkpoint disks, recorded children) looking for $target.
    private const string InUseGuard = """
        function Test-ControlChain([string]$Start) {
            $current = $Start
            for ($depth = 0; $current -and $depth -lt 64; $depth++) {
                try {
                    if ([string]::Equals([System.IO.Path]::GetFullPath($current), $target, [System.StringComparison]::OrdinalIgnoreCase)) { return $depth }
                    $current = [string](Get-VHD -Path $current -ErrorAction Stop).ParentPath
                } catch { return -1 }
            }
            return -1
        }
        $references = New-Object System.Collections.ArrayList
        foreach ($machine in @(Get-VM)) {
            $paths = @(Get-VMHardDiskDrive -VM $machine | ForEach-Object { [string]$_.Path })
            foreach ($snapshot in @(Get-VMSnapshot -VM $machine)) { $paths += @(Get-VMHardDiskDrive -VMSnapshot $snapshot | ForEach-Object { [string]$_.Path }) }
            foreach ($path in @($paths | Where-Object { $_ } | Select-Object -Unique)) {
                $depth = Test-ControlChain $path
                if ($depth -ge 0) { [void]$references.Add([ordered]@{ vm = $machine.Name; vmId = [string]$machine.Id; disk = $path; relation = $(if ($depth -eq 0) { 'attached' } else { 'differencingParent' }) }) }
            }
            foreach ($dvd in @(Get-VMDvdDrive -VM $machine)) {
                $same = $false
                try { $same = $dvd.Path -and [string]::Equals([System.IO.Path]::GetFullPath([string]$dvd.Path), $target, [System.StringComparison]::OrdinalIgnoreCase) } catch { }
                if ($same) { [void]$references.Add([ordered]@{ vm = $machine.Name; vmId = [string]$machine.Id; disk = [string]$dvd.Path; relation = 'attachedDvd' }) }
            }
        }
        foreach ($child in $children) {
            $depth = Test-ControlChain $child
            if ($depth -gt 0) { [void]$references.Add([ordered]@{ vm = $null; vmId = $null; disk = $child; relation = 'differencingParent' }) }
        }
        """;

    /// <summary>Adopts a local .vhdx or .iso in place. A template is made read-only, because it exists to be a differencing parent.</summary>
    public async Task<object> RegisterAsync(string? path, string? name, string? osFamily, string? kind, bool? secureBoot, string? notes, CancellationToken ct)
    {
        var file = ControlService.FullPath(path ?? throw new ArgumentException("path required: an absolute path to a local .vhdx or .iso."));
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100) throw new ArgumentException("name required (1-100 characters).");
        var entryName = name.Trim();
        var format = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".vhdx" => "vhdx",
            ".iso" => "iso",
            _ => throw new ArgumentException("register accepts a .vhdx or an .iso file.")
        };
        if (!File.Exists(file)) throw new HyperVOperationException($"File not found: {file}.", "NotFound");
        if (!ImageFiles.HasMagic(file, "." + format)) throw new HyperVOperationException($"{file} is not a valid .{format} (its signature does not match).", "UnsupportedImage");
        var entryKind = (kind ?? "image").Trim().ToLowerInvariant();
        if (!Kinds.Contains(entryKind)) throw new ArgumentException("kind must be image or template.");
        if (format == "iso" && entryKind == "template") throw new ArgumentException("An ISO is installation media; register it as kind=image.");
        var warnings = new List<string>();
        var family = osFamily?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(family))
        {
            family = ImageCatalogFeed.InferOsFamily(entryName + " " + Path.GetFileNameWithoutExtension(file), null, []);
            warnings.Add($"osFamily was not given; '{family}' was inferred from the name. Remove and register again with osFamily if that is wrong.");
        }
        else if (!OsFamilies.Contains(family)) throw new ArgumentException("osFamily must be windows, linux or other.");
        var store = await StoreAsync(ct);
        var entry = await store.UpdateLibraryAsync(index =>
        {
            if (index.Find(entryName) is not null) throw new HyperVOperationException($"A library entry named '{entryName}' already exists.", "AlreadyExists");
            if (index.Entries.FirstOrDefault(e => SamePath(e.Path, file)) is { } same) throw new HyperVOperationException($"{file} is already registered as '{same.Name}'.", "AlreadyExists");
            var template = entryKind == "template" && format == "vhdx";
            if (template) MakeReadOnly(file);
            var added = new ImageLibraryEntry(entryName, entryKind, family, file, format, new ImageSource(RegisteredFrom: file),
                new ImageSizes(new FileInfo(file).Length), new ImageConfig(secureBoot), Clock(), ReadOnly: template, Notes: notes);
            return (index with { Entries = [.. index.Entries, added] }, added);
        }, ct);
        return new { registered = true, entry = ProjectEntry(entry), warnings };
    }

    /// <summary>
    /// Removes a library entry (and with <paramref name="deleteFile"/> its file and kept archive). Refuses with
    /// <c>InUse</c> while any VM attaches the file, any disk chain (current, checkpoint or recorded child) has it as
    /// a differencing parent, or a VM's DVD drive holds it.
    /// </summary>
    public async Task<object> RemoveAsync(string? name, bool deleteFile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name required: the library entry to remove.");
        var store = await StoreAsync(ct);
        var entry = (await store.LibraryAsync(ct)).Find(name.Trim())
            ?? throw new HyperVOperationException($"No library entry named '{name}'. Call hyperv_image list.", "NotFound");
        var references = await ReferencesAsync(entry, ct);
        if (references.Count > 0)
        {
            var summary = string.Join("; ", references.Select(r =>
                $"{(r.TryGetProperty("vm", out var vm) && vm.ValueKind == JsonValueKind.String ? "VM '" + vm.GetString() + "'" : "an unattached disk")} "
                + $"{r.GetProperty("disk").GetString()} ({r.GetProperty("relation").GetString()})"));
            return new
            {
                ok = false, errorType = "InUse", references,
                error = $"'{entry.Name}' is in use: {summary}. Remove those VMs and child disks first; removing a differencing parent breaks its children."
            };
        }
        var deleted = new List<string>();
        if (deleteFile)
            foreach (var path in new[] { entry.Path, entry.ArchivePath })
                if (path is not null && File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    deleted.Add(path);
                }
        var removed = await store.UpdateLibraryAsync(index =>
        {
            var current = index.Find(entry.Name) ?? throw new HyperVOperationException($"No library entry named '{entry.Name}'.", "NotFound");
            return (index with { Entries = index.Entries.Where(e => !ReferenceEquals(e, current)).ToList() }, current);
        }, ct);
        return new
        {
            removed = removed.Name, deleted,
            kept = deleteFile ? null : removed.Path,
            note = !deleteFile && removed.ReadOnly ? "The file was kept with its read-only attribute." : null
        };
    }

    private async Task<List<JsonElement>> ReferencesAsync(ImageLibraryEntry entry, CancellationToken ct)
    {
        var children = (entry.Deployments ?? []).Select(d => d.DiskPath).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var script = new HyperVScript()
            .Line("$target = [System.IO.Path]::GetFullPath(" + PowerShellRunner.Literal(entry.Path) + ")")
            .Line("$children = @(" + string.Join(", ", children.Select(PowerShellRunner.Literal)) + ")")
            .Line(InUseGuard)
            .Returns("@($references)");
        var data = await Runner.InvokeAsync(script, ct, 300);
        return data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().Select(e => e.Clone()).ToList() : [];
    }

    private static void MakeReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) == 0) File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
    }
}
