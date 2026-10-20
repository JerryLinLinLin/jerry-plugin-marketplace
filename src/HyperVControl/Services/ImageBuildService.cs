using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static HyperVControl.Services.ImageBuildValidation;

namespace HyperVControl.Services;

/// <summary>
/// Golden image builds, run as background jobs. Windows: an ISO (local or a downloaded Microsoft evaluation) or an
/// install.wim/esd is applied offline to a GPT VHDX (EFI system partition, MSR, NTFS), made bootable with bcdboot, and
/// given a specialize/oobeSystem unattend.xml, optional offline kernel-debug BCD settings and a C:\Tools folder.
/// Linux: an Ubuntu cloud image is downloaded and checked against its SHA256SUMS, converted to a dynamic VHDX with
/// qemu-img, grown, and paired with a NoCloud (CIDATA) seed ISO. Each build writes &lt;name&gt;.image.json next to the
/// VHDX for the image library. Disk, DISM, bcdboot and IMAPI2 steps are PowerShell (<see cref="ImageBuildScripts"/>);
/// downloads, hashing, file writes and marking the BCD store as a system store (offreg.dll) are C#. Whatever a build
/// attaches is detached in every outcome, and a failed or cancelled build deletes its outputs (downloads stay cached for
/// the next attempt).
/// </summary>
internal sealed partial class ImageBuildService(PowerShellRunner runner, JobRunner jobs)
{
    internal PowerShellRunner Runner { get; } = runner;
    internal JobRunner Jobs { get; } = jobs;

    internal const string ImageRootVariable = "HYPERV_CONTROL_IMAGE_ROOT";
    internal const string QemuImgVariable = "HYPERV_CONTROL_QEMU_IMG";
    internal const string DefaultUbuntuImage = "https://cloud-images.ubuntu.com/releases/24.04/release/ubuntu-24.04-server-cloudimg-amd64.img";
    private const long Gigabyte = 1L << 30;
    private static readonly string[] QemuFormats = ["qcow2", "raw", "vmdk", "vpc", "vhdx", "vdi"];

    /// <summary>Anonymous Evaluation Center links (en-US); each resolves to a static CDN file.</summary>
    internal static readonly IReadOnlyDictionary<string, (Uri Url, string Description)> EvaluationMedia = new Dictionary<string, (Uri, string)>
    {
        ["eval"] = (new Uri("https://go.microsoft.com/fwlink/?LinkId=2382600&clcid=0x409&culture=en-us&country=us"), "Windows 11 Enterprise evaluation (26H2, about 7.7 GiB)"),
        ["eval-ltsc"] = (new Uri("https://go.microsoft.com/fwlink/?linkid=2289029&clcid=0x409&culture=en-us&country=us"), "Windows 11 Enterprise LTSC 2024 evaluation (about 4.8 GiB)"),
        ["eval-iot"] = (new Uri("https://go.microsoft.com/fwlink/?linkid=2270353&clcid=0x409&culture=en-us&country=us"), "Windows 11 IoT Enterprise LTSC 2024 evaluation (about 4.7 GiB)")
    };

    internal const string EvaluationWarning = "Evaluation media: the license runs 90 days. Give the first boot a switch with internet access so it activates; an expired or unactivated evaluation shows a black desktop and shuts down every hour.";
    internal const string PasswordInImageWarning = "Until a VM's first sign-in deletes it, Windows\\Panther\\unattend.xml inside the VHDX holds the admin password in plain text: treat the VHDX as sensitive.";
    internal const string QemuMissingMessage = "qemu-img was not found; converting the cloud image (QCOW2) to VHDX needs it. Install the QEMU disk image utility for Windows "
        + "(the Cloudbase build Microsoft documents: https://cloudbase.it/qemu-img-windows/, or QEMU for Windows), then set HYPERV_CONTROL_QEMU_IMG to the full path of "
        + "qemu-img.exe as a persistent user or machine environment variable (for example: setx HYPERV_CONTROL_QEMU_IMG \"C:\\Tools\\qemu-img\\qemu-img.exe\"), or put "
        + "qemu-img.exe on the system PATH. The MCP server started with --elevate forwards HYPERV_CONTROL_* variables from the client's environment to its elevated worker, so the client's own environment works too. A .vhdx imagePath needs no conversion.";

    /// <summary>Test seam: maps a drive letter the scripts report to the directory that C# file steps use.</summary>
    internal Func<char, string> DriveRoot { get; set; } = letter => letter + ":\\";
    internal IReadOnlyDictionary<string, Uri> EvaluationUrls { get; set; } = EvaluationMedia.ToDictionary(p => p.Key, p => p.Value.Url);
    internal Uri UbuntuImageUrl { get; set; } = new(DefaultUbuntuImage);
    internal HttpClient Http { get; set; } = SharedHttp.Value;
    private static readonly Lazy<HttpClient> SharedHttp = new(ImageBuildDownloader.CreateClient);
    private static readonly JsonSerializerOptions SidecarJson = new(Program.Json) { WriteIndented = true };
    private readonly ConcurrentDictionary<string, byte> activeOutputs = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?<!\S)N(?!\S)")] private static partial Regex NEdition();

    /// <summary>Validates the request, starts the build job and returns the job plus anything shown only once.</summary>
    public async Task<ImageBuildStart> StartAsync(ImageBuildRequest request, int waitSeconds, CancellationToken ct)
    {
        if (waitSeconds is < 0 or > 3600) throw new ArgumentException("waitSeconds must be 0..3600.");
        var plan = await PrepareAsync(request, ct);
        if (!activeOutputs.TryAdd(plan.VhdPath, 0)) throw new InvalidOperationException($"Another build is already writing {plan.VhdPath}.");
        var job = await Jobs.StartAndWaitAsync($"image-build {plan.OsFamily} {plan.Name}", async (progress, token) =>
        {
            try { return await ExecuteAsync(plan, progress, token); }
            finally { activeOutputs.TryRemove(plan.VhdPath, out _); }
        }, waitSeconds, ct);
        return Describe(plan, job);
    }

    internal static ImageBuildStart Describe(ImageBuildPlan plan, JobInfo job)
    {
        var failed = job.Status is JobStatus.Failed or JobStatus.Cancelled;
        object? credentials = null, kernelDebug = null;
        if (plan.Windows is { } w)
        {
            credentials = new ImageBuildCredentials(w.AdminUser, w.PasswordGenerated ? w.AdminPassword : null, w.PasswordGenerated,
                (w.PasswordGenerated
                    ? "This generated password appears only in this response and the plugin does not store it. "
                    : "The supplied password is not echoed back. ")
                + $"After you create a VM from this image and its first boot completes, store it with hyperv_credential (username {w.AdminUser}). {PasswordInImageWarning}");
            if (w.KernelDebug is { } kd)
                kernelDebug = new ImageBuildKernelDebugStart(kd.Transport, kd.HostIp, kd.Transport == "net" ? kd.Port : null, kd.Transport == "serial" ? kd.ComPort : null,
                    kd.Transport == "net" ? $"net:port={kd.Port},key={kd.Key}" : null, w.KeyGenerated,
                    kd.Transport == "net"
                        ? "The attach string (with its key) appears only in this response; the sidecar and job result omit the key. VMs from this image need Secure Boot off; attach with hyperv_debug_open mode=kernel."
                        : $"Before first boot map COM{kd.ComPort} to a pipe with hyperv_kernel_configure action=serial_host and attach with the string it returns. VMs from this image need Secure Boot off.");
        }
        else if (plan.Linux is { } l)
            credentials = new ImageBuildLinuxCredentials(l.SshUser, l.SshKeys.Count, l.PasswordHash is not null,
                "cloud-init creates this user with passwordless sudo; a password is stored only as a SHA-512-crypt hash in the seed ISO.");
        return new ImageBuildStart(!failed, failed ? job.Error : null, failed ? job.ErrorType : null, job,
            new ImageBuildOutputs(plan.VhdPath, plan.SidecarPath, plan.SeedIsoPath, plan.Source.Url is null ? null : plan.CacheDirectory),
            credentials, kernelDebug,
            job.Status == JobStatus.Succeeded
                ? "Done: the job result lists the outputs, warnings and nextSteps."
                : failed ? "The build failed. Its outputs were removed and nothing it attached stays mounted unless the error says otherwise; downloads stay cached for a retry."
                : $"Poll hyperv_jobs (action=wait or status, id={job.Id}) until the job finishes; its result lists the outputs, warnings and nextSteps.");
    }

    internal async Task<ImageBuildPlan> PrepareAsync(ImageBuildRequest r, CancellationToken ct)
    {
        var family = OsFamily(r.OsFamily);
        var name = Name(r.Name);
        RejectForeign(family, r);
        var sha = Sha256(r.Sha256);
        var warnings = new List<string>();
        ImageBuildPlan plan;
        if (family == "windows")
        {
            if (new[] { r.IsoPath, r.ImagePath, r.Source }.Count(v => v is not null) != 1)
                throw new ArgumentException("Supply exactly one Windows source: isoPath (a local .iso), imagePath (a local install.wim or install.esd), or source=eval|eval-ltsc|eval-iot "
                    + "(downloads the Microsoft evaluation ISO: about 7.7, 4.8 or 4.7 GiB, cached for later builds).");
            ImageBuildSourceSpec source;
            if (r.IsoPath is not null) source = new("iso", ExistingFile(r.IsoPath, "isoPath", ".iso"), null, null, sha);
            else if (r.ImagePath is not null) source = new("wim", ExistingFile(r.ImagePath, "imagePath", ".wim", ".esd"), null, null, sha);
            else
            {
                var key = r.Source!.Trim().ToLowerInvariant();
                if (!EvaluationUrls.TryGetValue(key, out var url)) throw new ArgumentException("source must be eval, eval-ltsc or eval-iot.");
                source = new("eval-download", null, url, key, sha);
                warnings.Add(EvaluationWarning);
            }
            if (r.ImageIndex is < 1) throw new ArgumentException("imageIndex must be 1 or greater.");
            if (r.ImageIndex is not null && r.Edition is not null) throw new ArgumentException("Supply imageIndex or edition, not both.");
            if (r.Edition is not null && (string.IsNullOrWhiteSpace(r.Edition) || r.Edition.Length > 128)) throw new ArgumentException("edition must be 1-128 characters of an image name.");
            var diskGb = DiskGb(r.DiskGb, DefaultWindowsDiskGb);
            if (diskGb < 64) warnings.Add($"Windows 11 requires a disk of at least 64 GB; diskGb={diskGb} suits tests only.");
            var computerName = ComputerName(r.ComputerName, name);
            var adminUser = WindowsUser(r.AdminUser);
            var generated = r.AdminPassword is null;
            var password = generated ? GeneratePassword() : Secret(r.AdminPassword!, "adminPassword", 127);
            var (kernelDebug, keyGenerated) = KernelDebug(r.KernelDebug);
            if (kernelDebug is not null) warnings.Add("Kernel debugging is enabled in the image's BCD store: VMs created from it need Secure Boot off (kernel debugging is not compatible with Secure Boot).");
            var language = Language(r.Language);
            var output = await ResolveOutputDirectoryAsync(r.OutputDirectory, ct);
            var tools = r.ToolsDirectory is null ? null : ToolsDirectory(r.ToolsDirectory, output);
            plan = new ImageBuildPlan
            {
                OsFamily = family, Name = name, OutputDirectory = output, VhdPath = Path.Combine(output, name + ".vhdx"),
                SidecarPath = Path.Combine(output, name + ".image.json"), DiskGb = diskGb, Source = source, Warnings = warnings,
                Windows = new ImageBuildWindowsSpec(r.ImageIndex, r.Edition?.Trim(), computerName, adminUser, password, generated,
                    r.EnableRdp ?? true, r.AutoLogon ?? true, r.PreventDeviceEncryption ?? true, kernelDebug, keyGenerated, tools, language)
            };
        }
        else
        {
            if (r.ImagePath is not null && r.ImageUrl is not null) throw new ArgumentException("Supply imageUrl or imagePath, not both.");
            ImageBuildSourceSpec source;
            bool convert;
            if (r.ImagePath is not null)
            {
                var path = ExistingFile(r.ImagePath, "imagePath", LinuxImageExtensions);
                source = new("image", path, null, null, sha);
                convert = !path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var url = r.ImageUrl is null ? UbuntuImageUrl : ImageUrl(r.ImageUrl, sha is not null);
                source = new("image-download", null, url, null, sha);
                convert = !url.AbsolutePath.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase);
            }
            var diskGb = DiskGb(r.DiskGb, DefaultLinuxDiskGb);
            var user = LinuxUser(r.SshUser);
            var keys = SshKeys(r.SshPublicKey);
            var passwordHash = r.Password is null ? null : ImageBuildCloudInit.Sha512Crypt(Secret(r.Password, "password", 256));
            if (keys.Count == 0 && passwordHash is null) throw new ArgumentException("sshPublicKey is required unless password is given.");
            var hostname = Hostname(r.Hostname, name);
            var packages = Packages(r.Packages);
            // Checked before anything is downloaded, so a missing converter costs nothing.
            var qemu = convert ? ResolveQemuImg() : null;
            var output = await ResolveOutputDirectoryAsync(r.OutputDirectory, ct);
            plan = new ImageBuildPlan
            {
                OsFamily = family, Name = name, OutputDirectory = output, VhdPath = Path.Combine(output, name + ".vhdx"),
                SidecarPath = Path.Combine(output, name + ".image.json"), SeedIsoPath = Path.Combine(output, name + "-seed.iso"),
                DiskGb = diskGb, Source = source, Warnings = warnings,
                Linux = new ImageBuildLinuxSpec(user, keys, passwordHash, hostname, packages, qemu)
            };
        }
        foreach (var existing in new[] { plan.VhdPath, plan.SidecarPath, plan.SeedIsoPath }.OfType<string>())
            if (File.Exists(existing) || Directory.Exists(existing))
                throw new ArgumentException($"{existing} already exists; choose another name or remove it first.");
        return plan;
    }

    internal async Task<string> ResolveOutputDirectoryAsync(string? requested, CancellationToken ct)
    {
        string directory;
        if (requested is not null) directory = LocalPath(requested, "outputDirectory");
        else if (Environment.GetEnvironmentVariable(ImageRootVariable) is { Length: > 0 } configured) directory = LocalPath(configured, ImageRootVariable);
        else
        {
            var vhdRoot = (await Runner.InvokeAsync(ImageBuildScripts.DefaultImageRoot(), ct)).GetString();
            if (string.IsNullOrWhiteSpace(vhdRoot)) throw new HyperVOperationException("Hyper-V reported no default virtual hard disk path; pass outputDirectory.", "InvalidResponse");
            directory = LocalPath(Path.Combine(vhdRoot, "HyperVControl", "Images"), "the host's default virtual hard disk path");
        }
        if (!Directory.Exists(Path.GetPathRoot(directory))) throw new ArgumentException($"The drive of {directory} does not exist.");
        return directory;
    }

    /// <summary>HYPERV_CONTROL_QEMU_IMG (process, then the persistent user and machine values), then PATH, then %ProgramFiles%\qemu.</summary>
    internal static string ResolveQemuImg()
    {
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var configured = Environment.GetEnvironmentVariable(QemuImgVariable, target);
            if (string.IsNullOrWhiteSpace(configured)) continue;
            var path = configured.Trim().Trim('"');
            if (Path.IsPathFullyQualified(path) && File.Exists(path)) return path;
            throw new HyperVOperationException($"{QemuImgVariable} ({target.ToString().ToLowerInvariant()} scope) is '{configured}', which is not an existing file; set it to the full path of qemu-img.exe.", "NotFound");
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), "qemu-img.exe");
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
        }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "qemu", "qemu-img.exe");
        return File.Exists(installed) ? installed : throw new HyperVOperationException(QemuMissingMessage, "NotFound");
    }

    internal async Task<object?> ExecuteAsync(ImageBuildPlan plan, IProgress<JobProgress> progress, CancellationToken ct) =>
        plan.OsFamily == "windows" ? await BuildWindowsAsync(plan, progress, ct) : await BuildLinuxAsync(plan, progress, ct);

    private sealed record ImageInfo(int Index, string Name, string EditionId, string InstallationType, int Architecture, string Version, long Size,
        string[] Languages, int DefaultLanguageIndex);

    private async Task<ImageBuildResult> BuildWindowsAsync(ImageBuildPlan plan, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var w = plan.Windows!;
        var warnings = new List<string>(plan.Warnings);
        void Phase(int step, string text) => progress.Report(new JobProgress(Math.Round(step * 100.0 / 10, 1), Message: $"windows {step}/10: {text}"));
        Directory.CreateDirectory(plan.OutputDirectory);

        Phase(1, "resolving the source image");
        var (sourceFile, builtFrom) = await ResolveSourceAsync(plan, warnings, progress, ct);
        var isoPath = plan.Source.Kind is "iso" or "eval-download" ? sourceFile : null;

        var mountedIso = false;
        var diskStarted = false;
        Exception? failure = null;
        ImageInfo? image = null;
        ImageBuildPartition[] partitions = [];
        string? bootConfiguration = null, bootSummary = null;
        bool? debugApplied = null;
        ImageBuildToolsCopy? tools = null;
        try
        {
            string imageFile;
            if (isoPath is not null)
            {
                Phase(2, "mounting the ISO read-only");
                var probe = await Runner.InvokeAsync(ImageBuildScripts.ProbeIso(isoPath), ct, 120);
                char letter;
                if (probe.GetProperty("attached").GetBoolean())
                {
                    letter = Letter(probe, "driveLetter") ?? throw new HyperVOperationException("The ISO is already mounted without a drive letter; dismount it or give it a letter, then retry.", "InvalidArgument");
                    warnings.Add($"The ISO was already mounted as {letter}:; the build used it in place and left it mounted.");
                }
                else
                {
                    mountedIso = true;
                    letter = Letter(await Runner.InvokeAsync(ImageBuildScripts.MountIso(isoPath), ct, 180), "driveLetter")
                        ?? throw new HyperVOperationException("Mount-DiskImage reported no drive letter.", "InvalidResponse");
                }
                imageFile = LocateInstallImage(letter) ?? throw new HyperVOperationException(
                    "The ISO has no sources\\install.wim or sources\\install.esd (split .swm media is not supported); pass the install image with imagePath.", "NotFound");
            }
            else imageFile = sourceFile;

            Phase(3, "reading the image list");
            var index = SelectImage(await Runner.InvokeAsync(ImageBuildScripts.ListImages(imageFile), ct, 900), w.ImageIndex, w.Edition);
            image = ReadImage(await Runner.InvokeAsync(ImageBuildScripts.ImageDetails(imageFile, index), ct, 900));
            var uiLanguage = CheckImage(image, plan, warnings);

            Phase(4, "creating the GPT VHDX (EFI system, MSR and Windows partitions)");
            diskStarted = true;
            var disk = await Runner.InvokeAsync(ImageBuildScripts.CreateDisk(plan.VhdPath, plan.DiskGb), ct, 900);
            var system = Letter(disk, "systemLetter") ?? throw new HyperVOperationException("The disk script returned no system partition letter.", "InvalidResponse");
            var windows = Letter(disk, "windowsLetter") ?? throw new HyperVOperationException("The disk script returned no Windows partition letter.", "InvalidResponse");
            GuardLetters(system, windows);
            partitions = disk.GetProperty("partitions").EnumerateArray().Select(p => new ImageBuildPartition(p.GetProperty("number").GetInt32(),
                p.GetProperty("gptType").GetString() ?? "", p.GetProperty("size").GetInt64(), p.GetProperty("driveLetter").GetString())).ToArray();

            Phase(5, $"applying image {index} ({image.Name}); this takes minutes");
            await Runner.InvokeAsync(ImageBuildScripts.ApplyImage(imageFile, index, windows), ct, 4 * 3600);
            var windowsRoot = DriveRoot(windows);
            if (!Directory.Exists(Path.Combine(windowsRoot, "Windows")))
                throw new HyperVOperationException("The image was applied but contains no \\Windows directory; it is not a Windows installation image.", "HyperVOperationFailed");

            Phase(6, "writing boot files and the BCD store (bcdboot /s, UEFI)");
            (bootConfiguration, bootSummary) = await MakeBootableAsync(windows, system, warnings, ct);

            Phase(7, "writing Windows\\Panther\\unattend.xml");
            ImageBuildUnattend.Write(Path.Combine(windowsRoot, "Windows", "Panther", "unattend.xml"), new UnattendSettings(w.ComputerName, w.AdminUser,
                w.AdminPassword, w.EnableRdp, w.AutoLogon, w.PreventDeviceEncryption, w.Language, w.Language, uiLanguage, w.Language));

            if (w.KernelDebug is { } kd)
            {
                Phase(8, $"writing kernel-debug settings ({kd.Transport}) into the offline BCD store");
                debugApplied = (await Runner.InvokeAsync(ImageBuildScripts.BcdDebug(system, kd), ct, 300)).GetProperty("debugOn").GetBoolean();
                if (debugApplied != true) warnings.Add("bcdedit accepted the debug settings, but reading {default} back did not show 'debug Yes'; check with hyperv_kernel_configure action=status after first boot.");
                // bcdedit /store clears TreatAsSystem when it closes the store, whichever step created it.
                var store = Path.Combine(DriveRoot(system), "EFI", "Microsoft", "Boot", "BCD");
                if (File.Exists(store)) MarkSystemStore(store);
            }
            if (w.ToolsDirectory is { } toolsDirectory)
            {
                Phase(8, "copying the tools folder to C:\\Tools");
                tools = CopyTree(toolsDirectory, Path.Combine(windowsRoot, "Tools"), "C:\\Tools");
            }
        }
        catch (Exception ex) { failure = ex; }

        Phase(9, "dismounting");
        await FinishAsync(plan, failure, diskStarted ? plan.VhdPath : null, mountedIso ? isoPath : null);

        Phase(10, "writing the sidecar");
        var config = new ImageBuildVmConfig(2, w.KernelDebug is null, "MicrosoftWindows", "VMBus", true, 4096, true, 2, "Standard", false);
        var sidecar = new ImageBuildSidecar(1, plan.Name, "windows", "template", plan.VhdPath, null, config, w.AdminUser, null, builtFrom, DateTimeOffset.UtcNow,
            new ImageBuildImageRecord(image!.Index, image.Name, NullIfEmpty(image.EditionId), NullIfEmpty(image.Version), image.Architecture < 0 ? null : image.Architecture, image.Size, image.Languages),
            w.KernelDebug is { } debug ? new ImageBuildKernelDebugRecord(debug.Transport, debug.HostIp, debug.Transport == "net" ? debug.Port : null, debug.Transport == "serial" ? debug.ComPort : null, false) : null,
            bootConfiguration, warnings);
        return Complete(plan, sidecar, partitions, bootConfiguration, bootSummary, debugApplied, tools, warnings, WindowsNextSteps(plan), plan.DiskGb * Gigabyte, null, clock);
    }

    private async Task<ImageBuildResult> BuildLinuxAsync(ImageBuildPlan plan, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var l = plan.Linux!;
        var warnings = new List<string>(plan.Warnings);
        void Phase(int step, string text) => progress.Report(new JobProgress(Math.Round(step * 100.0 / 5, 1), Message: $"linux {step}/5: {text}"));
        Directory.CreateDirectory(plan.OutputDirectory);

        Phase(1, "resolving the cloud image");
        var (source, builtFrom) = await ResolveSourceAsync(plan, warnings, progress, ct);
        Exception? failure = null;
        long? seedBytes = null;
        long virtualBytes = 0;
        string? staging = null;
        try
        {
            if (l.QemuImg is null)
            {
                Phase(2, "copying the VHDX");
                await Task.Run(() => File.Copy(source, plan.VhdPath, overwrite: false), ct);
            }
            else
            {
                Phase(2, "converting to a dynamic VHDX with qemu-img");
                var info = await Runner.InvokeAsync(ImageBuildScripts.QemuInfo(l.QemuImg, source), ct, 300);
                var format = info.GetProperty("format").GetString() ?? "";
                if (!string.IsNullOrEmpty(info.GetProperty("backingFile").GetString()))
                    throw new HyperVOperationException("The image references a backing file; only standalone images are converted.", "InvalidArgument");
                if (!QemuFormats.Contains(format))
                    throw new HyperVOperationException($"qemu-img reports format '{format}'; expected one of {string.Join(", ", QemuFormats)}.", "InvalidArgument");
                await Runner.InvokeAsync(ImageBuildScripts.QemuConvert(l.QemuImg, source, format, plan.VhdPath), ct, 3600);
            }

            Phase(3, $"growing the virtual disk to {plan.DiskGb} GB");
            var resize = await Runner.InvokeAsync(ImageBuildScripts.ResizeVhd(plan.VhdPath, plan.DiskGb), ct, 600);
            virtualBytes = resize.GetProperty("size").GetInt64();
            if (resize.GetProperty("sizeBefore").GetInt64() > plan.DiskGb * Gigabyte)
                warnings.Add($"The image's virtual disk ({resize.GetProperty("sizeBefore").GetInt64() / (double)Gigabyte:0.#} GB) is larger than diskGb={plan.DiskGb}; it was kept, because disks are never shrunk.");

            Phase(4, "building the NoCloud seed ISO (CIDATA)");
            staging = Path.Combine(Path.GetTempPath(), "HyperVControl-seed-" + Guid.NewGuid().ToString("N"));
            ImageBuildCloudInit.WriteSeed(staging, ImageBuildCloudInit.UserData(l.SshUser, l.SshKeys, l.PasswordHash, l.Hostname, l.Packages),
                ImageBuildCloudInit.MetaData("iid-" + Guid.NewGuid().ToString("N"), l.Hostname));
            seedBytes = (await Runner.InvokeAsync(ImageBuildScripts.BuildIso(staging, ImageBuildCloudInit.VolumeLabel, plan.SeedIsoPath!), ct, 300)).GetProperty("size").GetInt64();
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            if (staging is not null)
                try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        await FinishAsync(plan, failure, null, null);

        Phase(5, "writing the sidecar");
        var config = new ImageBuildVmConfig(2, true, "MicrosoftUEFICertificateAuthority", "HvSocket", false, 2048, true, 2, "Standard", false);
        var sidecar = new ImageBuildSidecar(1, plan.Name, "linux", "template", plan.VhdPath, plan.SeedIsoPath, config, null, l.SshUser, builtFrom,
            DateTimeOffset.UtcNow, null, null, null, warnings);
        return Complete(plan, sidecar, null, null, null, null, null, warnings, LinuxNextSteps(plan), virtualBytes, seedBytes, clock);
    }

    /// <summary>Detaches what the build attached; a failed or cancelled build then loses its outputs and rethrows.</summary>
    private async Task FinishAsync(ImageBuildPlan plan, Exception? failure, string? vhdx, string? iso)
    {
        var problems = new List<string>();
        if (vhdx is not null || iso is not null)
        {
            try
            {
                var cleanup = await Runner.InvokeAsync(ImageBuildScripts.Cleanup(vhdx, iso), CancellationToken.None, 300);
                problems.AddRange(cleanup.GetProperty("problems").EnumerateArray().Select(p => p.GetString() ?? ""));
            }
            catch (Exception ex) { problems.Add("Dismount failed: " + ex.Message); }
        }
        if (failure is null)
        {
            if (problems.Count > 0)
                throw new HyperVOperationException($"The image was built but could not be detached ({string.Join("; ", problems)}). Run Dismount-VHD -Path '{plan.VhdPath}' before using it.", "HyperVOperationFailed");
            return;
        }
        foreach (var path in new[] { plan.VhdPath, plan.SidecarPath, plan.SeedIsoPath }.OfType<string>())
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{path} could not be deleted: {ex.Message}"); }
        }
        if (failure is OperationCanceledException || problems.Count == 0) ExceptionDispatchInfo.Throw(failure);
        throw new HyperVOperationException(failure.Message + " Cleanup: " + string.Join("; ", problems), (failure as HyperVOperationException)?.ErrorType ?? "HyperVOperationFailed");
    }

    private static ImageBuildResult Complete(ImageBuildPlan plan, ImageBuildSidecar sidecar, IReadOnlyList<ImageBuildPartition>? partitions, string? bootConfiguration,
        string? bootSummary, bool? debugApplied, ImageBuildToolsCopy? tools, List<string> warnings, IReadOnlyList<string> nextSteps, long virtualBytes, long? seedBytes, Stopwatch clock)
    {
        if (!File.Exists(plan.VhdPath)) throw new HyperVOperationException($"The build finished but {plan.VhdPath} is missing.", "HyperVOperationFailed");
        WriteSidecar(plan.SidecarPath, sidecar);
        return new ImageBuildResult(plan.OsFamily, plan.Name, plan.VhdPath, new FileInfo(plan.VhdPath).Length, virtualBytes, plan.SeedIsoPath, seedBytes,
            plan.SidecarPath, sidecar, partitions, bootConfiguration, bootSummary, debugApplied, tools, warnings, nextSteps, Math.Round(clock.Elapsed.TotalSeconds, 1));
    }

    internal static void WriteSidecar(string path, ImageBuildSidecar sidecar)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(sidecar, SidecarJson) + "\n", new UTF8Encoding(false));
        File.Move(temporary, path);
    }

    private async Task<(string Path, ImageBuildSourceRecord Record)> ResolveSourceAsync(ImageBuildPlan plan, List<string> warnings, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var source = plan.Source;
        if (source.Url is { } url)
        {
            var downloader = new ImageBuildDownloader(Http);
            var expected = source.ExpectedSha256;
            var checksumSource = expected is null ? null : "sha256 parameter";
            if (expected is null && plan.OsFamily == "linux")
            {
                expected = await downloader.PublishedSha256Async(url, ct)
                    ?? throw new HyperVOperationException($"No SHA256SUMS entry is published next to {url}; pass sha256 to verify this image.", "NotFound");
                checksumSource = new Uri(url, "SHA256SUMS").ToString();
            }
            var download = await downloader.DownloadAsync(url, plan.CacheDirectory, expected, progress, source.EvalSource ?? "image", ct);
            if (expected is null)
                warnings.Add($"Microsoft publishes no checksum for evaluation ISOs; the download's SHA-256 ({download.Sha256}) is recorded in the sidecar.");
            return (download.Path, new ImageBuildSourceRecord(source.Kind, download.Path, url.ToString(), download.FinalUrl.ToString(), source.EvalSource,
                download.Sha256, download.Bytes, expected is not null, checksumSource, download.Reused));
        }
        var hash = await ImageBuildDownloader.HashAsync(source.Path!, progress, "source", ct);
        if (source.ExpectedSha256 is not null && hash != source.ExpectedSha256)
            throw new InvalidDataException($"SHA-256 mismatch for {source.Path}: expected {source.ExpectedSha256}, got {hash}.");
        return (source.Path!, new ImageBuildSourceRecord(source.Kind, source.Path, null, null, null, hash, new FileInfo(source.Path!).Length,
            source.ExpectedSha256 is not null, source.ExpectedSha256 is null ? null : "sha256 parameter", false));
    }

    internal string? LocateInstallImage(char letter)
    {
        foreach (var name in new[] { "install.wim", "install.esd" })
            if (File.Exists(Path.Combine(DriveRoot(letter), "sources", name))) return letter + ":\\sources\\" + name;
        return null;
    }

    /// <summary>imageIndex, else edition (exact name, else one unique substring match), else the first edition that is not an N edition.</summary>
    internal static int SelectImage(JsonElement list, int? index, string? edition)
    {
        var images = (list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : [list])
            .Select(i => (Index: i.GetProperty("index").GetInt32(), Name: i.GetProperty("name").GetString() ?? "")).ToList();
        if (images.Count == 0) throw new HyperVOperationException("The install image contains no images.", "NotFound");
        var listing = string.Join("; ", images.Select(i => $"{i.Index}: {i.Name}"));
        if (index is not null)
            return images.Any(i => i.Index == index) ? index.Value : throw new HyperVOperationException($"imageIndex {index} does not exist. Available: {listing}.", "NotFound");
        if (edition is not null)
        {
            var exact = images.Where(i => i.Name.Equals(edition, StringComparison.OrdinalIgnoreCase)).ToList();
            var matches = exact.Count > 0 ? exact : images.Where(i => i.Name.Contains(edition, StringComparison.OrdinalIgnoreCase)).ToList();
            return matches.Count switch
            {
                1 => matches[0].Index,
                0 => throw new HyperVOperationException($"No image name matches edition '{edition}'. Available: {listing}.", "NotFound"),
                _ => throw new HyperVOperationException($"edition '{edition}' matches several images ({string.Join("; ", matches.Select(m => $"{m.Index}: {m.Name}"))}); pass imageIndex.", "AmbiguousName")
            };
        }
        return images.Where(i => !NEdition().IsMatch(i.Name)).Select(i => (int?)i.Index).FirstOrDefault() ?? images[0].Index;
    }

    private static ImageInfo ReadImage(JsonElement e) => new(e.GetProperty("index").GetInt32(), e.GetProperty("name").GetString() ?? "",
        e.GetProperty("editionId").GetString() ?? "", e.GetProperty("installationType").GetString() ?? "", e.GetProperty("architecture").GetInt32(),
        e.GetProperty("version").GetString() ?? "", e.GetProperty("size").GetInt64(),
        e.GetProperty("languages").ValueKind == JsonValueKind.Array ? e.GetProperty("languages").EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length > 0).ToArray() : [],
        e.GetProperty("defaultLanguageIndex").GetInt32());

    /// <summary>Architecture, size, free space, evaluation and language checks; returns the UI language to use.</summary>
    private string CheckImage(ImageInfo image, ImageBuildPlan plan, List<string> warnings)
    {
        var identified = image.EditionId.Length > 0 || image.Version is { Length: > 0 } and not "0.0.0.0";
        switch (image.Architecture)
        {
            case 9: break;
            case 5 or 12: throw new HyperVOperationException($"Image {image.Index} is an ARM image; an x64 Hyper-V host cannot boot it.", "InvalidArgument");
            case 6: throw new HyperVOperationException($"Image {image.Index} is an Itanium image.", "InvalidArgument");
            case 0 when identified: throw new HyperVOperationException($"Image {image.Index} is 32-bit (x86); Generation 2 VMs need x64 Windows.", "InvalidArgument");
            default: warnings.Add($"Image {image.Index} carries no Windows version metadata (architecture {image.Architecture}); it was treated as a custom x64 capture."); break;
        }
        var windowsBytes = plan.DiskGb * Gigabyte - (ImageBuildScripts.EspMegabytes + ImageBuildScripts.MsrMegabytes + 2) * (1L << 20);
        if (image.Size > windowsBytes * 0.95)
            throw new HyperVOperationException($"Image {image.Index} expands to {image.Size / (double)Gigabyte:0.0} GB but diskGb={plan.DiskGb} leaves {windowsBytes / (double)Gigabyte:0.0} GB for Windows; raise diskGb.", "InvalidArgument");
        var drive = new DriveInfo(Path.GetPathRoot(plan.OutputDirectory)!);
        var needed = (long)(image.Size * 1.1) + Gigabyte;
        if (drive.AvailableFreeSpace < needed)
            throw new HyperVOperationException($"{drive.Name} has {drive.AvailableFreeSpace / (double)Gigabyte:0.0} GB free; the applied image needs about {needed / (double)Gigabyte:0.0} GB.", "InvalidArgument");
        if (plan.Source.Kind != "eval-download" && (image.Name.Contains("Evaluation", StringComparison.OrdinalIgnoreCase) || image.EditionId.Contains("Eval", StringComparison.OrdinalIgnoreCase)))
            warnings.Add(EvaluationWarning);
        var language = plan.Windows!.Language;
        if (image.Languages.Length == 0 || image.Languages.Contains(language, StringComparer.OrdinalIgnoreCase)) return language;
        var fallback = image.Languages[Math.Clamp(image.DefaultLanguageIndex, 0, image.Languages.Length - 1)];
        warnings.Add($"The image has no {language} language pack ({string.Join(", ", image.Languages)}); the display language is {fallback} and {language} is used for input, system and user locales.");
        return fallback;
    }

    /// <summary>
    /// bcdboot first. This host family's bcdboot can copy the boot files yet fail to create a store on another disk's
    /// EFI partition (BFSVC status c0000035: the new store is loaded at the key the host's own store occupies); only in
    /// that case, and only when the boot files were copied, the store is completed with bcdedit from the image's template
    /// and then marked as a system store (<see cref="MarkSystemStore"/>), which the template is not.
    /// </summary>
    private async Task<(string Method, string Summary)> MakeBootableAsync(char windows, char system, List<string> warnings, CancellationToken ct)
    {
        var boot = await Runner.InvokeAsync(ImageBuildScripts.BcdBoot(windows, system), ct, 600);
        var exitCode = boot.GetProperty("exitCode").GetInt32();
        var output = boot.GetProperty("output").GetString() ?? "";
        var method = boot.GetProperty("offline").GetBoolean() ? "bcdboot /offline" : "bcdboot";
        var summary = SummarizeBcdBoot(output);
        if (exitCode == 0) return (method, summary);
        var espRoot = DriveRoot(system);
        var bootDirectory = Path.Combine(espRoot, "EFI", "Microsoft", "Boot");
        var template = Path.Combine(DriveRoot(windows), "Windows", "System32", "config", "BCD-Template");
        var filesCopied = File.Exists(Path.Combine(espRoot, "EFI", "Boot", "bootx64.efi")) && File.Exists(Path.Combine(bootDirectory, "bootmgfw.efi"));
        if (!IsStoreCollision(output) || !filesCopied || !File.Exists(template))
            throw new HyperVOperationException($"bcdboot failed (exit {exitCode}): {summary}", "HyperVOperationFailed");
        foreach (var stale in Directory.GetFiles(bootDirectory, "BCD*")) File.Delete(stale);
        var store = Path.Combine(bootDirectory, "BCD");
        File.Copy(template, store);
        await Runner.InvokeAsync(ImageBuildScripts.BcdStore(windows, system, DefaultLanguage, Guid.NewGuid(), Guid.NewGuid()), ct, 600);
        MarkSystemStore(store);
        warnings.Add("bcdboot copied the boot files but could not create the BCD store on the image's EFI partition on this host (BFSVC c0000035: it loads the new store at "
            + "the registry key the host's own store occupies). The store was completed with bcdedit from the image's BCD-Template instead: the boot manager plus one "
            + "Windows Boot Loader with the elements bcdboot sets, marked as a system store (System and TreatAsSystem) as bcdboot marks its own. A VM's first boot is the confirmation.");
        return (method + " (files) + bcdedit (store)", summary);
    }

    internal static bool IsStoreCollision(string output) =>
        output.Contains("c0000035", StringComparison.OrdinalIgnoreCase) && output.Contains("store", StringComparison.OrdinalIgnoreCase);

    /// <summary>Errors, warnings and the final line of bcdboot's verbose output.</summary>
    internal static string SummarizeBcdBoot(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var important = lines.Where(line => line.Contains("Error", StringComparison.OrdinalIgnoreCase) || line.Contains("Warning", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Failure", StringComparison.OrdinalIgnoreCase)).Take(12).ToList();
        if (lines.Length > 0 && !important.Contains(lines[^1])) important.Add(lines[^1]);
        return important.Count == 0 ? "(no output)" : string.Join(" | ", important);
    }

    private const string SystemStoreKeyName = "BCD00000000";
    private const int HiveBaseBlockBytes = 4096;

    /// <summary>
    /// Marks an offline BCD store as a system store, as bcdboot marks the stores it creates: <c>Description\KeyName</c> =
    /// BCD00000000 (REG_SZ) and <c>Description\System</c> = <c>Description\TreatAsSystem</c> = 1 (REG_DWORD). The image's
    /// BCD-Template sets neither flag, and <c>bcdedit /store</c> clears TreatAsSystem whenever it closes a store; unmarked,
    /// the VM's boot manager stops with 0xc000000e and bcdboot rejects the store (0xc0000098, not a system store). The hive
    /// is edited in memory with the inbox offline registry library (offreg.dll: no registry load, no privilege), saved whole
    /// to &lt;store&gt;.tmp and moved over the store. The store's transaction logs (&lt;store&gt;.LOG*) are then deleted:
    /// they describe the file bcdedit left, and a log must never be replayed onto the new one.
    /// </summary>
    internal static void MarkSystemStore(string storePath)
    {
        RequireHive(storePath);
        var temporary = storePath + ".tmp";
        try
        {
            // ORSaveHive never overwrites, so a temporary file left by an interrupted run goes first.
            ImageFiles.TryDelete(temporary);
            var hive = OfflineRegistry.OpenHive(storePath);
            try
            {
                var description = OfflineRegistry.CreateKey(hive, "Description", storePath);
                try
                {
                    OfflineRegistry.SetString(description, "KeyName", SystemStoreKeyName, storePath);
                    OfflineRegistry.SetDword(description, "System", 1, storePath);
                    OfflineRegistry.SetDword(description, "TreatAsSystem", 1, storePath);
                }
                finally { OfflineRegistry.CloseKey(description); }
                OfflineRegistry.SaveHive(hive, temporary);
            }
            finally { OfflineRegistry.CloseHive(hive); }
            // A move over a file drops its hidden and system attributes and fails on a read-only one: clear, move, restore.
            var attributes = File.GetAttributes(storePath);
            File.SetAttributes(storePath, FileAttributes.Normal);
            File.Move(temporary, storePath, overwrite: true);
            File.SetAttributes(storePath, attributes);
        }
        finally { ImageFiles.TryDelete(temporary); }
        var logs = Path.GetFileName(storePath) + ".LOG";
        foreach (var log in Directory.GetFiles(Path.GetDirectoryName(storePath)!, logs + "*"))
        {
            if (!Path.GetFileName(log).StartsWith(logs, StringComparison.OrdinalIgnoreCase)) continue;
            File.SetAttributes(log, FileAttributes.Normal);
            File.Delete(log);
        }
    }

    /// <summary>
    /// The system-store values of an offline BCD store (<c>Description\KeyName</c>, <c>System</c>, <c>TreatAsSystem</c>),
    /// read with offreg.dll without loading or changing the file; an absent value reads as "" or -1. Used by the tests.
    /// </summary>
    internal static (string KeyName, int System, int TreatAsSystem) ReadSystemStoreFlags(string storePath)
    {
        RequireHive(storePath);
        var hive = OfflineRegistry.OpenHive(storePath);
        try
        {
            if (OfflineRegistry.OpenKey(hive, "Description", storePath) is not { } description) return ("", -1, -1);
            try
            {
                return (OfflineRegistry.GetString(description, "KeyName", storePath) ?? "",
                    OfflineRegistry.GetDword(description, "System", storePath) ?? -1,
                    OfflineRegistry.GetDword(description, "TreatAsSystem", storePath) ?? -1);
            }
            finally { OfflineRegistry.CloseKey(description); }
        }
        finally { OfflineRegistry.CloseHive(hive); }
    }

    /// <summary>Writes a new registry hive with no keys or values; the tests use it as a BCD-Template or store stand-in.</summary>
    internal static void CreateEmptyHive(string path)
    {
        var hive = OfflineRegistry.CreateHive();
        try { OfflineRegistry.SaveHive(hive, path); }
        finally { OfflineRegistry.CloseHive(hive); }
    }

    /// <summary>Fails unless the file begins with a registry hive's base block: the "regf" signature and at least 4 KB.</summary>
    private static void RequireHive(string path)
    {
        if (!File.Exists(path)) throw new HyperVOperationException($"The BCD store {path} does not exist.", "NotFound");
        Span<byte> signature = stackalloc byte[4];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (stream.Length >= HiveBaseBlockBytes && stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false) == signature.Length
                && signature.SequenceEqual("regf"u8))
                return;
        throw new HyperVOperationException($"{path} is not a registry hive (it has no 'regf' base block), so it is not a BCD store.", "HyperVOperationFailed");
    }

    /// <summary>
    /// The offline registry library that ships with Windows (System32\offreg.dll, loaded from there only). It reads and
    /// writes a hive file in memory with plain file access, never through the host's registry, so no store is ever loaded
    /// at a key the host's own store holds. Each function returns a Win32 error code; any but 0 becomes a
    /// <see cref="HyperVOperationException"/>.
    /// </summary>
    private static class OfflineRegistry
    {
        private const int RegSz = 1, RegDword = 4, ErrorFileNotFound = 2;

        public static nint CreateHive()
        {
            Check(ORCreateHive(out var hive), "ORCreateHive", "a new hive");
            return hive;
        }

        public static nint OpenHive(string path)
        {
            Check(OROpenHive(path, out var hive), "OROpenHive", path);
            return hive;
        }

        /// <summary>Saves in the hive format of Windows 7 and later (6.1, regf 1.5); the target file must not exist.</summary>
        public static void SaveHive(nint hive, string path) => Check(ORSaveHive(hive, path, 6, 1), "ORSaveHive", path);

        public static void CloseHive(nint hive) => ORCloseHive(hive);

        /// <summary>The subkey's handle, or null when it does not exist.</summary>
        public static nint? OpenKey(nint parent, string name, string path)
        {
            var status = OROpenKey(parent, name, out var key);
            if (status == ErrorFileNotFound) return null;
            Check(status, "OROpenKey " + name, path);
            return key;
        }

        /// <summary>Opens the subkey, creating it when it does not exist.</summary>
        public static nint CreateKey(nint parent, string name, string path)
        {
            Check(ORCreateKey(parent, name, null, 0, 0, out var key, out _), "ORCreateKey " + name, path);
            return key;
        }

        public static void CloseKey(nint key) => ORCloseKey(key);

        public static void SetString(nint key, string name, string value, string path)
        {
            var data = Encoding.Unicode.GetBytes(value + "\0");
            Check(ORSetValue(key, name, RegSz, data, data.Length), "ORSetValue " + name, path);
        }

        public static void SetDword(nint key, string name, int value, string path) =>
            Check(ORSetValue(key, name, RegDword, BitConverter.GetBytes(value), sizeof(int)), "ORSetValue " + name, path);

        public static string? GetString(nint key, string name, string path) =>
            Get(key, name, RegSz, path) is { } data ? Encoding.Unicode.GetString(data).TrimEnd('\0') : null;

        public static int? GetDword(nint key, string name, string path) =>
            Get(key, name, RegDword, path) is { } data ? BitConverter.ToInt32(data) : null;

        /// <summary>A value's data, or null when the value does not exist; a value of another registry type fails.</summary>
        private static byte[]? Get(nint key, string name, int type, string path)
        {
            var size = 0;
            var status = ORGetValue(key, null, name, out _, null, ref size);
            if (status == ErrorFileNotFound) return null;
            Check(status, "ORGetValue " + name, path);
            var data = new byte[size];
            Check(ORGetValue(key, null, name, out var actual, data, ref size), "ORGetValue " + name, path);
            if (actual != type || (type == RegDword && size != sizeof(int)))
                throw new HyperVOperationException($"The registry value {name} in {path} has type {actual} ({size} bytes), not {(type == RegDword ? "REG_DWORD" : "REG_SZ")}.", "InvalidResponse");
            return data[..size];
        }

        private static void Check(int status, string operation, string path)
        {
            if (status != 0)
                throw new HyperVOperationException($"{operation} failed for {path}: {new Win32Exception(status).Message} (Win32 error {status}).", "HyperVOperationFailed");
        }

        [DllImport("offreg.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int ORCreateHive(out nint hive);

        [DllImport("offreg.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int OROpenHive(string path, out nint hive);

        [DllImport("offreg.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int ORSaveHive(nint hive, string path, int osMajorVersion, int osMinorVersion);

        [DllImport("offreg.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int ORCloseHive(nint hive);

        [DllImport("offreg.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int OROpenKey(nint parent, string subKey, out nint key);

        [DllImport("offreg.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int ORCreateKey(nint parent, string subKey, string? className, int options, nint securityDescriptor, out nint key, out int disposition);

        [DllImport("offreg.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int ORCloseKey(nint key);

        [DllImport("offreg.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int ORSetValue(nint key, string name, int type, byte[] data, int size);

        [DllImport("offreg.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int ORGetValue(nint key, string? subKey, string name, out int type, byte[]? data, ref int size);
    }

    private static void GuardLetters(char system, char windows)
    {
        var host = char.ToUpperInvariant(Path.GetPathRoot(Environment.SystemDirectory)![0]);
        if (system == windows || system == host || windows == host)
            throw new HyperVOperationException($"Unexpected drive letters for the new partitions (system {system}:, Windows {windows}:).", "HyperVOperationFailed");
    }

    private static char? Letter(JsonElement e, string property) =>
        e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is [var c] && char.IsAsciiLetter(c)
            ? char.ToUpperInvariant(c) : null;

    /// <summary>
    /// Copies a folder tree (hidden files included, reparse points skipped) into the image. An existing folder there is
    /// merged, but a file the image already has is never overwritten: that fails the build.
    /// </summary>
    internal static ImageBuildToolsCopy CopyTree(string source, string destination, string guestPath)
    {
        if (File.Exists(destination)) throw new HyperVOperationException($"{guestPath} is a file in the image.", "InvalidArgument");
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", options))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        int files = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", options))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            if (File.Exists(target)) throw new HyperVOperationException($"{guestPath}\\{relative} already exists in the image; tools never overwrite image files.", "InvalidArgument");
            File.Copy(file, target, overwrite: false);
            files++;
            bytes += new FileInfo(file).Length;
        }
        return new ImageBuildToolsCopy(guestPath, files, bytes);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    internal static IReadOnlyList<string> WindowsNextSteps(ImageBuildPlan plan)
    {
        var w = plan.Windows!;
        var steps = new List<string>
        {
            $"Keep {plan.VhdPath} unchanged as the template: give each VM a differencing child (New-VHD -Differencing -ParentPath '{plan.VhdPath}') or a full copy. Booting the template itself runs specialize and consumes its unattend.",
            "Create the VM with hyperv_vm_create: source=existing_vhd, vhdPath=<the child disk>, generation=2, memoryMb=4096 (static), processors=2, and switchName set to a switch with internet access (for example Default Switch). It is created Off.",
            "Before the first start: hyperv_vm_configure dynamicMemory=false checkpointType=Standard; turn automatic checkpoints off; "
                + (w.KernelDebug is null ? "Secure Boot On with the MicrosoftWindows template" : "Secure Boot Off (hyperv_firmware secureBoot=false), because the image has kernel debugging enabled")
                + "; add a vTPM with a local key protector (Set-VMKeyProtector -NewLocalKeyProtector, then Enable-VMTPM).",
            $"Start the VM. The first boot runs specialize and OOBE unattended (several minutes, possibly one restart){(w.AutoLogon ? $" and signs {w.AdminUser} in automatically" : "")}.",
            $"When PowerShell Direct answers, store the credential: hyperv_credential machine=<vm> username={w.AdminUser} password=<the password returned when this build started>."
        };
        if (w.KernelDebug is { } kd)
            steps.Add(kd.Transport == "net"
                ? $"Kernel debugging (KDNET to {kd.HostIp}:{kd.Port}) is preconfigured: allow inbound UDP {kd.Port} on the host firewall and attach with hyperv_debug_open mode=kernel connectString=<kernelAttachString from the build start>."
                : $"Kernel debugging (serial, COM{kd.ComPort}) is preconfigured: while the VM is Off run hyperv_kernel_configure action=serial_host comPort={kd.ComPort}, then attach with hyperv_debug_open mode=kernel and the returned attach string.");
        steps.Add("Evaluation images expire 90 days after first boot and must activate online: keep the first boot on a NAT or internet switch.");
        return steps;
    }

    internal static IReadOnlyList<string> LinuxNextSteps(ImageBuildPlan plan)
    {
        var l = plan.Linux!;
        return
        [
            $"Keep {plan.VhdPath} unchanged as the template: give each VM a differencing child or a full copy. Children of the never-booted template can share the seed ISO (and so its hostname).",
            "Create the VM with hyperv_vm_create: source=existing_vhd, vhdPath=<the child disk>, generation=2, memoryMb=2048, processors=2, osFamily=linux, switchName=<a switch with DHCP and internet access>.",
            "Before the first start: Secure Boot On with the MicrosoftUEFICertificateAuthority template (Set-VMFirmware -SecureBootTemplate MicrosoftUEFICertificateAuthority; the Windows template rejects the Linux shim); checkpointType=Standard; automatic checkpoints off.",
            $"Attach the seed as a DVD: hyperv_disk action=attach_iso path={plan.SeedIsoPath}.",
            $"Start the VM. cloud-init creates {l.SshUser}, grows the root file system and installs {(l.Packages.Count == 0 ? "no packages" : string.Join(", ", l.Packages))}; linux-cloud-tools-virtual lets hyperv_vm_info report the guest IP (KVP). Then: ssh {l.SshUser}@<ip>."
        ];
    }
}

internal sealed record ImageBuildToolsCopy(string GuestPath, int Files, long Bytes);
internal sealed record ImageBuildOutputs(string VhdPath, string SidecarPath, string? SeedIsoPath, string? CacheDirectory);
internal sealed record ImageBuildCredentials(string AdminUser, string? Password, bool PasswordGenerated, string Warning);
internal sealed record ImageBuildLinuxCredentials(string SshUser, int SshKeys, bool PasswordSet, string Note);
internal sealed record ImageBuildKernelDebugStart(string Transport, string? HostIp, int? Port, int? ComPort, string? KernelAttachString, bool KeyGenerated, string Note);

/// <summary>The <c>hyperv_image_build</c> result: the job snapshot plus anything returned only once (a generated password, the KDNET key).</summary>
internal sealed record ImageBuildStart(bool Ok, string? Error, string? ErrorType, JobInfo Job, ImageBuildOutputs Outputs, object? Credentials, object? KernelDebug, string Next);
