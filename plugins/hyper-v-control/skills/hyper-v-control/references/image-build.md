# Golden image builds

`hyperv_image_build` turns OS media into a template VHDX that new VMs start from. It never creates a VM. Build once per OS release, keep the VHDX unchanged, and give each VM a differencing child (`New-VHD -Differencing -ParentPath <template>`) or a full copy: booting the template itself runs its first-boot setup, and changing a parent breaks its children. Prefer media the user already has (an ISO, an install.wim/esd, a cloud image) over a download, and confirm before downloading evaluation media of several GiB.

## Running a build

The call validates every argument before touching the host, then starts a background job and returns `{ ok, job, outputs, credentials, kernelDebug, next }`. Pass `waitSeconds` (up to 3600) to wait in the same call, or poll `hyperv_jobs` with the job ID; progress reports the phase and download bytes. A Windows build from local media takes minutes (DISM apply dominates); Linux takes about as long as its download and conversion.

Outputs go to `outputDirectory`, an absolute local path (default: `HYPERV_CONTROL_IMAGE_ROOT`, else `<Hyper-V default VHD path>\HyperVControl\Images`): `<name>.vhdx`, `<name>.image.json`, and for Linux `<name>-seed.iso`. The sidecar `<name>.image.json` records the OS family, kind `template`, the VM settings the image expects (generation, Secure Boot and its template, vTPM, enhanced-session transport, memory, processors, checkpoint type), the admin or SSH user, the source with its SHA-256, the build time and the warnings. The job result repeats it and adds `warnings` and `nextSteps`; follow them. Existing outputs are refused: choose another `name` or remove the old files. A failed or cancelled build detaches everything it attached and deletes its outputs; downloads stay in `<outputDirectory>\cache`, resume on the next attempt, and are reused when they still match.

## Windows

Give exactly one source: `isoPath` (a local ISO), `imagePath` (install.wim or install.esd) or `source`: `eval` (Windows 11 Enterprise 26H2, about 7.7 GiB), `eval-ltsc` (Enterprise LTSC 2024, about 4.8 GiB) or `eval-iot` (IoT Enterprise LTSC 2024, about 4.7 GiB), the anonymous en-US [Evaluation Center](https://www.microsoft.com/en-us/evalcenter/download-windows-11-enterprise) downloads. Select an edition with `imageIndex` or `edition` (exact name, else one unique substring); the default is the first edition that is not an N edition. ARM and 32-bit images are refused.

Windows Setup never runs. The build mounts the ISO read-only, creates a dynamic GPT VHDX of `diskGb` (default 100; Windows 11 needs at least 64) with a 300 MB FAT32 EFI system partition, a 16 MB MSR and an NTFS Windows partition, applies the image with DISM, makes it bootable with `bcdboot <W>:\Windows /s <S>: /f UEFI`, writes `Windows\Panther\unattend.xml` and detaches everything. With `/s` bcdboot writes no firmware entry on the host; `/offline` (added when the host's bcdboot supports it) selects the image's standard boot manager regardless of the host's own Secure Boot state. Some hosts' bcdboot copies the boot files but cannot create the store on another disk (BFSVC status `c0000035`); the build then completes the store with bcdedit from the image's own BCD template and says so in `warnings`. Any other bcdboot failure fails the build with bcdboot's reason.

The first boot runs specialize and OOBE unattended and produces:

- a local administrator `adminUser` (default `labadmin`) with the admin password, signed in automatically unless `autoLogon=false`;
- the computer name `computerName` (default derived from `name`);
- Remote Desktop and its firewall group on unless `enableRdp=false` (Enhanced Session needs RDP and a non-blank password);
- automatic device encryption prevented unless `preventDeviceEncryption=false`, so Secure Boot or BCD changes never trigger BitLocker recovery;
- Windows Hello-only sign-in and password expiry off, OOBE network and account pages skipped, and the answer file deleted at the first sign-in;
- `language` (default en-US) for input, system and user locales; the display language falls back to the image's own when it lacks that language pack;
- optionally `C:\Tools` from `toolsDirectory`, and kernel debugging from `kernelDebug`: `{enabled: true, transport: "net", hostIp, port, key}` or `{enabled: true, transport: "serial", comPort}`, written into the image's BCD store before first boot. Kernel debugging is incompatible with Secure Boot, so VMs from such an image need Secure Boot off; see [debugging](debugging.md) for attaching.

Password rule: when `adminPassword` is omitted, a 20-character password is generated and returned exactly once, as `credentials.password` in the response that starts the build. The job result, the sidecar and the logs never contain it, and the plugin does not store it. Give it to the user, and once a VM from the image has finished its first boot, store it with `hyperv_credential`. A supplied password is never echoed back. A generated KDNET key is likewise returned only once, inside `kernelDebug.kernelAttachString`. Until a VM's first sign-in deletes it, the unattend inside the VHDX holds the password in plain text: treat the VHDX as sensitive.

Evaluation media are licensed for 90 days and must activate online: give the first boot a NAT or internet switch. An expired or unactivated evaluation shows a black desktop and shuts down every hour. The build warns when the image is an evaluation edition.

Create the VM as Generation 2 with the child disk, static memory of 4096 MB, two processors, Standard checkpoints and automatic checkpoints off; Secure Boot on with the `MicrosoftWindows` template (off for kernel-debug images); and a vTPM with a local key protector. Then start it, wait until PowerShell Direct answers, and store the credential.

## Ubuntu

The default source is the Ubuntu 24.04 server cloud image (QCOW2, about 0.6 GiB), verified against Canonical's published `SHA256SUMS`. `imageUrl` (https; http only with `sha256`) or `imagePath` chooses another; a URL without a published checksum needs `sha256`.

Converting QCOW2 (or raw, vmdk, vhd) to VHDX needs qemu-img, the one non-inbox dependency; the Microsoft-documented build is Cloudbase's [qemu-img for Windows](https://cloudbase.it/qemu-img-windows/). The server finds it through `HYPERV_CONTROL_QEMU_IMG`, then the PATH, then `%ProgramFiles%\qemu\qemu-img.exe`. Set it in the MCP client's environment or as a persistent user or machine variable (for example `setx HYPERV_CONTROL_QEMU_IMG "C:\Tools\qemu-img\qemu-img.exe"`); the server forwards `HYPERV_CONTROL_*` variables to the elevated worker. Without qemu-img the build is refused before anything is downloaded. A `.vhdx` `imagePath` needs no conversion and is copied. Images that reference a backing file are refused.

The VHDX is grown to `diskGb` (default 40; never shrunk) and cloud-init grows the root file system at first boot. The seed ISO is a NoCloud volume labelled `CIDATA` (ISO 9660 plus Joliet, built with the inbox IMAPI2 API) holding `user-data` and `meta-data` with LF line endings: user `sshUser` (default `lab`) with passwordless sudo and `sshPublicKey` (required unless `password` is given); an optional `password`, stored only as a SHA-512-crypt hash, which enables SSH password login only when no key is given; `hostname`; `packages` (default `linux-cloud-tools-virtual`, whose KVP daemon reports the guest IP to Hyper-V); and a fresh instance-id.

Create the VM as Generation 2 with the child disk, 2048 MB of memory and two processors; set Secure Boot on with the `MicrosoftUEFICertificateAuthority` template (the Windows template rejects the Linux boot loader); attach the seed ISO as a DVD (`hyperv_disk` action `attach_iso`); start it. When `hyperv_vm_info` shows an address, connect with `ssh <sshUser>@<ip>`. Children of the never-booted template can share the seed ISO, but they then share its hostname; a template that has already booted needs `cloud-init clean` and a seed with a new instance-id for each VM.
