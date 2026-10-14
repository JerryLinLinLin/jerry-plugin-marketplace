using System.Diagnostics;
using System.Text.Json;
using HyperVControl.Services;

namespace HyperVControl.Tests;

/// <summary>
/// Live validation of the image workflow (phase <c>images-live</c>), authorized for one Ubuntu 22.04 fetch from
/// Microsoft's gallery (2.5 GB zip, 6.4 GB VHDX) and throwaway VMs: list, fetch as a polled job, the in-use guard
/// on the real module, a linked-clone deploy that boots to the first-boot wizard (heartbeat plus a basic-console
/// screenshot), then removal of the VM, its disk, the library entry and every downloaded byte, even on failure.
/// Run it from an elevated shell with HYPERV_CONTROL_IMAGE_ROOT set: a worker started by --elevate does not inherit
/// process environment variables. Only VMs named HyperVControl-validation-images-* are ever operated on.
/// </summary>
internal static class ImageLiveTests
{
    private const string ImageName = "Ubuntu 22.04 LTS";
    private const string Prefix = "HyperVControl-validation-images-";
    private const string JammySha256 = "eb6b904aa40f7214bdeade25e7b189db8c25a021b711677c1049d61867ff20cf";

    internal static async Task RunAsync(ToolCall call, Action<bool, string> check, string results)
    {
        var root = Environment.GetEnvironmentVariable(ImageCatalogService.RootVariable)
            ?? throw new InvalidOperationException($"Set {ImageCatalogService.RootVariable} (for example D:\\HyperVControl-validation\\images) for this phase.");
        if (!ElevationBridge.IsAdministrator)
            throw new InvalidOperationException("Run images-live from an elevated shell: the --elevate worker would not inherit " + ImageCatalogService.RootVariable + ".");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var vmRoot = root + "-vms";
        var vmName = Prefix + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var clock = Stopwatch.StartNew();
        var steps = new List<object>();
        var progress = new List<object>();
        var screenshots = new List<object>();
        var evidence = new Dictionary<string, object?> { ["root"] = root, ["vmRoot"] = vmRoot, ["vmName"] = vmName, ["image"] = ImageName, ["startedAt"] = DateTimeOffset.UtcNow };
        void Step(string name, object? detail = null)
        {
            steps.Add(new { name, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), detail });
            Console.WriteLine($"STEP {clock.Elapsed.TotalSeconds,6:0}s {name}");
        }
        void Pass(bool condition, string message)
        {
            check(condition, message);
            steps.Add(new { name = "PASS " + message, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), detail = (object?)null });
        }

        string? vmId = null;
        Exception? failure = null;
        try
        {
            var before = await call("hyperv_list", label: "images-live-inventory-before");
            var existing = before.GetProperty("machines").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();
            evidence["vmsBefore"] = existing;
            Pass(!existing.Contains(vmName), "The throwaway VM name is unused");

            // 1. List: Microsoft's feed, warnings and the recommendation; no disk URL is touched.
            var list = await call("hyperv_image", new { action = "list" }, label: "images-live-list");
            var images = list.GetProperty("images").EnumerateArray().ToList();
            var jammy = images.Single(i => i.GetProperty("catalog").GetString() == "microsoft" && i.GetProperty("name").GetString() == ImageName);
            var windev = images.Single(i => i.GetProperty("name").GetString() == "Windows 11 dev environment");
            evidence["list"] = new
            {
                catalogs = list.GetProperty("catalogs"), recommended = list.GetProperty("recommended"),
                images = images.Select(i => new { name = i.GetProperty("name").GetString(), expired = i.GetProperty("expired").GetBoolean(), warnings = i.GetProperty("warnings") }).ToList()
            };
            Pass(jammy.GetProperty("fetchable").GetBoolean() && jammy.GetProperty("disk").GetProperty("sha256").GetString() == JammySha256
                && windev.GetProperty("expired").GetBoolean() && list.GetProperty("recommended").GetProperty("linux").GetProperty("name").GetString() == ImageName,
                "list reads Microsoft's live feed, flags the expired Windows image and recommends Ubuntu 22.04");

            // 2. Fetch as a job and poll it.
            var started = await call("hyperv_image", new { action = "fetch", imageName = ImageName, catalog = "microsoft" }, label: "images-live-fetch-start");
            JsonElement job;
            if (started.TryGetProperty("alreadyInLibrary", out _)) throw new InvalidOperationException($"{root} already holds {ImageName}; clean the validation root before this phase.");
            var jobId = started.GetProperty("id").GetString()!;
            Step("fetch started", jobId);
            var sawDownloading = false;
            do
            {
                job = await call("hyperv_jobs", new { action = "wait", jobId, waitSeconds = 20 }, label: "images-live-fetch-poll");
                var p = job.GetProperty("progress");
                sawDownloading |= p.GetProperty("message").GetString() == "downloading";
                progress.Add(new
                {
                    seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), status = job.GetProperty("status").GetString(), message = p.GetProperty("message").GetString(),
                    percent = p.GetProperty("percent").ValueKind == JsonValueKind.Number ? p.GetProperty("percent").GetDouble() : (double?)null,
                    bytesDone = p.GetProperty("bytesDone").ValueKind == JsonValueKind.Number ? p.GetProperty("bytesDone").GetInt64() : (long?)null,
                    bytesTotal = p.GetProperty("bytesTotal").ValueKind == JsonValueKind.Number ? p.GetProperty("bytesTotal").GetInt64() : (long?)null
                });
            }
            while (job.GetProperty("status").GetString() is "queued" or "running");
            evidence["fetch"] = job;
            Pass(job.GetProperty("status").GetString() == "succeeded", "The fetch job succeeds: " + job.GetProperty("error"));
            var entry = job.GetProperty("result").GetProperty("entry");
            var imagePath = entry.GetProperty("path").GetString()!;
            var cache = Path.Combine(root, "cache");
            Pass(entry.GetProperty("format").GetString() == "vhdx" && imagePath.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                && new FileInfo(imagePath).Length > 6_000_000_000L && ImageFiles.HasMagic(imagePath, ".vhdx")
                && entry.GetProperty("source").GetProperty("sha256").GetString() == JammySha256
                && entry.GetProperty("config").GetProperty("secureBoot").GetBoolean() == false
                && (!Directory.Exists(cache) || Directory.GetFiles(cache).Length == 0) && sawDownloading,
                "Fetched, verified and extracted a 6+ GB VHDX into the library; the archive was deleted; progress was observable");

            // 3. Deploy a linked clone with the lab defaults and start it.
            var deployed = await call("hyperv_image", new
            {
                action = "deploy", imageName = ImageName, vmName, vmPath = vmRoot, memoryMb = 4096, processors = 2, start = true,
                notes = "HyperVControl images-live validation VM; safe to delete."
            }, label: "images-live-deploy");
            vmId = deployed.GetProperty("vm").GetProperty("Id").GetString();
            evidence["deploy"] = deployed;
            var vm = deployed.GetProperty("vm");
            Pass(deployed.GetProperty("disk").GetProperty("type").GetString() == "differencing"
                && string.Equals(deployed.GetProperty("disk").GetProperty("parentPath").GetString(), imagePath, StringComparison.OrdinalIgnoreCase)
                && deployed.GetProperty("firmware").GetProperty("SecureBoot").GetString() == "Off"
                && vm.GetProperty("EnhancedSessionTransportType").GetString() == "HvSocket" && !vm.GetProperty("DynamicMemoryEnabled").GetBoolean()
                && !vm.GetProperty("AutomaticCheckpointsEnabled").GetBoolean() && vm.GetProperty("CheckpointType").GetString() == "Standard"
                && deployed.GetProperty("started").GetBoolean() && File.GetAttributes(imagePath).HasFlag(FileAttributes.ReadOnly),
                "Deploy creates a differencing child, applies the lab defaults (Secure Boot off per the feed, HvSocket, static memory, Standard checkpoints) and starts the VM");

            var guarded = await call("hyperv_image", new { action = "remove", name = ImageName, deleteFile = true }, allowError: true, label: "images-live-remove-guarded");
            Pass(guarded.GetProperty("errorType").GetString() == "InUse" && File.Exists(imagePath)
                && guarded.GetProperty("references").EnumerateArray().Any(r => r.GetProperty("relation").GetString() == "differencingParent"),
                "The real module's parent chain makes remove refuse the image while the clone exists");

            // 4. Boot evidence: heartbeat from the guest kernel and the first-boot wizard on the basic console.
            await call("hyperv_console", new { action = "open", machine = vmId, session = "basic" }, label: "images-live-console-open");
            var heartbeat = "";
            long largest = 0;
            for (var attempt = 1; attempt <= 16; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                var label = $"images-live-screenshot-{attempt:00}";
                await call("hyperv_screenshot", new { machine = vmId }, label: label);
                var png = Path.Combine(results, label + ".png");
                var size = File.Exists(png) ? new FileInfo(png).Length : 0;
                largest = Math.Max(largest, size);
                var info = await call("hyperv_vm_info", new { machine = vmId }, label: "images-live-vm-info");
                heartbeat = info.GetProperty("integration").EnumerateArray()
                    .Where(s => s.GetProperty("Name").GetString() == "Heartbeat").Select(s => s.GetProperty("PrimaryStatusDescription").GetString()).FirstOrDefault() ?? "";
                screenshots.Add(new { attempt, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), png = label + ".png", bytes = size, heartbeat });
                if (heartbeat == "OK" && size > 40_000 && attempt >= 6) break;
            }
            evidence["screenshots"] = screenshots;
            Pass(heartbeat == "OK" && largest > 40_000, $"The clone boots: heartbeat {heartbeat}, console framebuffer {largest} bytes of PNG (see the saved screenshots)");
            await call("hyperv_console", new { action = "close", machine = vmId }, allowError: true, label: "images-live-console-close");

            // 5. Stop, remove the VM and its disk, then the library entry and the image file.
            await call("hyperv_power", new { machine = vmId, action = "turn_off" }, label: "images-live-turn-off");
            await call("hyperv_vm_remove", new { machineId = vmId }, label: "images-live-vm-remove");
            var removedId = vmId;
            vmId = null;
            await DeleteVmFolderAsync(vmRoot, vmName);
            var after = await call("hyperv_list", label: "images-live-inventory-after");
            Pass(!after.GetProperty("machines").EnumerateArray().Any(m => m.GetProperty("id").GetString() == removedId) && !Directory.Exists(Path.Combine(vmRoot, vmName)),
                "The throwaway VM and its differencing disk are gone");
            var removed = await call("hyperv_image", new { action = "remove", name = ImageName, deleteFile = true }, label: "images-live-remove");
            Pass(!File.Exists(imagePath) && removed.GetProperty("deleted").GetArrayLength() == 1, "The library entry and its image file are removed");
            Step("done");
        }
        catch (Exception ex)
        {
            failure = ex;
            evidence["failure"] = ex.ToString();
        }
        finally
        {
            // Cleanup runs whether or not the checks passed, and touches only this phase's VM and folders.
            try { await CleanupAsync(call, vmId, vmName, vmRoot, root, steps); }
            catch (Exception ex) { evidence["cleanupFailure"] = ex.ToString(); failure ??= ex; }
            evidence["steps"] = steps;
            evidence["progress"] = progress;
            evidence["screenshots"] = screenshots;
            evidence["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
            evidence["leftovers"] = new[] { root, vmRoot }.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFileSystemEntries(d, "*", SearchOption.AllDirectories)).ToList();
            await File.WriteAllTextAsync(Path.Combine(results, "images-live-evidence.json"),
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        }
        if (failure is not null) throw new Exception("images-live failed; cleanup ran. " + failure.Message, failure);
    }

    private static async Task CleanupAsync(ToolCall call, string? vmId, string vmName, string vmRoot, string root, List<object> steps)
    {
        // Find this phase's VM by its unique name if the deploy result was lost; never act on any other VM.
        var inventory = await call("hyperv_list", allowError: true, label: "images-live-cleanup-inventory");
        if (inventory.TryGetProperty("machines", out var machines))
        {
            foreach (var machine in machines.EnumerateArray())
            {
                var name = machine.GetProperty("name").GetString() ?? "";
                var id = machine.GetProperty("id").GetString();
                if (!name.StartsWith(Prefix, StringComparison.Ordinal) || (name != vmName && id != vmId)) continue;
                await call("hyperv_console", new { action = "close", machine = id }, allowError: true, label: "images-live-cleanup-console");
                await call("hyperv_power", new { machine = id, action = "turn_off" }, allowError: true, label: "images-live-cleanup-turn-off");
                await call("hyperv_vm_remove", new { machineId = id }, allowError: true, label: "images-live-cleanup-vm-remove");
                steps.Add(new { name = "cleanup removed VM " + id, seconds = 0.0, detail = (object?)null });
            }
        }
        await DeleteVmFolderAsync(vmRoot, vmName);
        await call("hyperv_image", new { action = "remove", name = ImageName, deleteFile = true }, allowError: true, label: "images-live-cleanup-remove");
        // The validation root and VM folder belong to this phase only; drop everything left in them (partials, indexes).
        foreach (var directory in new[] { root, vmRoot })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }

    /// <summary>Deletes this phase's VM folder; VMMS can hold the disk briefly after Remove-VM, so it retries.</summary>
    private static async Task DeleteVmFolderAsync(string vmRoot, string vmName)
    {
        var folder = Path.Combine(vmRoot, vmName);
        if (!vmName.StartsWith(Prefix, StringComparison.Ordinal)) return;
        for (var attempt = 1; Directory.Exists(folder); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(folder, true);
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 10) { await Task.Delay(3000); }
        }
    }
}
