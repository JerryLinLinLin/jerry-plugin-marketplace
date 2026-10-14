using System.ComponentModel;
using HyperVControl.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HyperVControl;

/// <summary>Golden image build tool (<c>hyperv_image_build</c>): validates the request and starts a background job.</summary>
internal sealed partial class HyperVTools
{
    [McpServerTool(Name = "hyperv_image_build")]
    [Description("Build a golden template VHDX as a background job, without creating a VM. windows: apply an ISO (isoPath), install.wim/esd (imagePath) or a downloaded "
        + "Microsoft evaluation ISO (source=eval|eval-ltsc|eval-iot, 4.7-7.7 GiB, cached, 90-day license) offline to a GPT VHDX with an unattend: local admin, RDP, "
        + "autologon, no device encryption, optional offline kernel-debug BCD settings and C:\\Tools. linux: convert an Ubuntu cloud image (default 24.04, checked against "
        + "SHA256SUMS; needs qemu-img) to VHDX and build a NoCloud CIDATA seed ISO. Writes <name>.image.json beside the VHDX and refuses existing outputs. A generated "
        + "password is returned once, in this response only. Poll hyperv_jobs for the result and nextSteps.")]
    public Task<CallToolResult> ImageBuild(
        [Description("windows or linux.")] string osFamily,
        [Description("Output base name: <name>.vhdx and <name>.image.json (plus <name>-seed.iso for linux) in outputDirectory.")] string name,
        [Description("Absolute local directory. Default: HYPERV_CONTROL_IMAGE_ROOT, else <Hyper-V default VHD path>\\HyperVControl\\Images. Downloads are cached in its cache subfolder.")] string? outputDirectory = null,
        [Description("windows: local Windows .iso.")] string? isoPath = null,
        [Description("windows: eval (Windows 11 Enterprise 26H2), eval-ltsc (Enterprise LTSC 2024) or eval-iot (IoT Enterprise LTSC 2024) evaluation ISO download, resumable.")] string? source = null,
        [Description("windows: local install.wim or install.esd. linux: local cloud image (.img/.qcow2/.raw/.vmdk/.vhd are converted with qemu-img; .vhdx is copied).")] string? imagePath = null,
        [Description("linux: https URL of a cloud image; default the Ubuntu 24.04 server cloud image, verified against its published SHA256SUMS.")] string? imageUrl = null,
        [Description("Expected SHA-256 of the ISO or image; required for http:// URLs. The actual hash is recorded in the sidecar either way.")] string? sha256 = null,
        [Description("windows: image index inside install.wim/esd.")] int? imageIndex = null,
        [Description("windows: image name to select instead of imageIndex (exact, else a unique substring). Default: the first non-N edition.")] string? edition = null,
        [Description("Virtual disk size in GB (dynamic VHDX). Default 100 for windows, 40 for linux.")] int? diskGb = null,
        [Description("windows: computer name, at most 15 letters/digits/hyphens, or * for random. Default: derived from name.")] string? computerName = null,
        [Description("windows: local administrator created by the unattend. Default labadmin.")] string? adminUser = null,
        [Description("windows: administrator password. When omitted a strong one is generated and returned once.")] string? adminPassword = null,
        [Description("windows: enable Remote Desktop and its firewall rule (Enhanced Session needs it). Default true.")] bool? enableRdp = null,
        [Description("windows: sign the administrator in automatically. Default true.")] bool? autoLogon = null,
        [Description("windows: prevent automatic device encryption (BitLocker). Default true.")] bool? preventDeviceEncryption = null,
        [Description("windows: offline BCD kernel debugging {enabled, transport: net|serial, hostIp, port (default 50000), key (generated when omitted), comPort}. VMs from such an image need Secure Boot off.")] ImageBuildKernelDebug? kernelDebug = null,
        [Description("windows: absolute host folder copied into the image as C:\\Tools.")] string? toolsDirectory = null,
        [Description("windows: locale for input, system and user settings and the display language. Default en-US.")] string? language = null,
        [Description("linux: user that cloud-init creates with passwordless sudo. Default lab.")] string? sshUser = null,
        [Description("linux: OpenSSH public key line(s) for sshUser; required unless password is given.")] string? sshPublicKey = null,
        [Description("linux: password for sshUser, stored only as a SHA-512-crypt hash; enables SSH password login only when no key is given.")] string? password = null,
        [Description("linux: host name. Default: derived from name.")] string? hostname = null,
        [Description("linux: packages installed at first boot. Default [linux-cloud-tools-virtual] (KVP, so Hyper-V can report the guest IP).")] string[]? packages = null,
        [Description("Seconds (0-3600) to wait for the job before returning; then poll hyperv_jobs.")] int waitSeconds = 0,
        CancellationToken ct = default) => Run(async () => await service.ImageBuild.StartAsync(new ImageBuildRequest
        {
            OsFamily = osFamily, Name = name, OutputDirectory = outputDirectory, IsoPath = isoPath, Source = source, ImagePath = imagePath,
            ImageUrl = imageUrl, Sha256 = sha256, ImageIndex = imageIndex, Edition = edition, DiskGb = diskGb, ComputerName = computerName,
            AdminUser = adminUser, AdminPassword = adminPassword, EnableRdp = enableRdp, AutoLogon = autoLogon,
            PreventDeviceEncryption = preventDeviceEncryption, KernelDebug = kernelDebug, ToolsDirectory = toolsDirectory, Language = language,
            SshUser = sshUser, SshPublicKey = sshPublicKey, Password = password, Hostname = hostname, Packages = packages
        }, waitSeconds, ct));
}
