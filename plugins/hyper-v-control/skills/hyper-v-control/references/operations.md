# VM operations

Inventory uses local Hyper-V, not network discovery. Every `machine` argument takes an exact name or GUID; ambiguous names fail, so use the ID once a name is duplicated. Results carry the VM's GUID.

## What the VM's state allows

Hardware tools check the VM's state before changing anything. A call is all-or-nothing: when one requested setting is not allowed, the error (`InvalidVmState`) names every such setting with the reason and nothing is applied. Drop those settings or turn the VM off and retry; never fall back to a hard power-off on your own.

| State | Allowed |
| --- | --- |
| Off | Everything. |
| Running | Static memory resize (`memoryMb` without dynamic memory; the guest OS must be booted and support it, otherwise Hyper-V refuses and nothing changes), dynamic memory `memoryMaxMb` increases, `memoryMinMb` decreases and `memoryBufferPercent`; `checkpointType`, `autoCheckpoints`, `newName`, `notes`, `autoStartAction`, `autoStartDelaySeconds`, `integrationServices`; adapter connect/disconnect, MAC spoofing and VLAN; Generation 2 adapter add/remove; SCSI data-disk attach/detach (never the boot disk); ISO insert/eject and a Generation 2 DVD drive add; online resize of a VHDX on SCSI; the COM port pipe (a Generation 2 guest sees a newly mapped port only after the VM is turned off and started again). |
| Saved | Rename, removal, export and checkpoint operations only. |
| Paused, transitional or critical | Nothing: resume, or wait for Off or Running. |

Off only: vCPU count (Hyper-V has no vCPU hot add), turning dynamic memory on or off, `autoStopAction`, nested virtualization, the enhanced-session transport, MAC addresses, everything in `hyperv_firmware`, IDE disks, and cloning.

## Create

`hyperv_vm_create` leaves the VM Off unless `start=true`. Defaults: `source=image`, Generation 2, 4096 MB static memory, 2 vCPUs, Standard checkpoints with automatic checkpoints off, and the Default Switch (else the first switch; pass an empty `switchName` for no network). The VM folder is `<path>\<name>`; `path` defaults to the host's VirtualMachinePath (see `hyperv_capabilities` `hostDefaults`). New disks go to `<path>\<name>\Virtual Hard Disks` when `path` is given, otherwise to the host's VirtualHardDiskPath; an image VM's disk always goes to `<path>\<name>\Virtual Hard Disks`.

Sources:

- `image` (default): a linked clone (a differencing child, ready in seconds) of `imageName` or, without it, of the default image: `HYPERV_CONTROL_DEFAULT_IMAGE`, else Microsoft's Windows 11 dev environment, a Windows 11 Enterprise evaluation. `imageName` is a library entry, else a catalog image (`catalog` disambiguates). An image the library lacks is fetched first in one `hyperv_jobs` job that then creates the VM; its `result` is the VM, `waitSeconds` (0-3600, default 60) bounds the wait, and a still-running job comes back with `next: "hyperv_jobs action=wait jobId=<id>"`. The Windows image's first use downloads 23.3 GB and extracts a 44.8 GB VHDX (about 69 GB free at the peak): confirm with the user first; `hyperv_image list` shows `defaultImage.inLibrary`. The name, disk path and switch are checked before the download, and a running fetch of the same image is joined. The VM takes the image's OS family and Secure Boot setting with the lab defaults (static memory, Standard checkpoints, automatic checkpoints off, a vTPM for Windows); `vhdPath`, `parentPath`, `isoPath`, `generation`, `diskGb`, dynamic memory, `secureBoot`, `tpm`, `autoCheckpoints` and `checkpointType` are refused (use another source, or change the clone afterwards). See [images](images.md#default-image).
- `new_disk`: an empty 64 GB dynamic VHDX (`diskGb`); attach installation media before the first start.
- `iso`: a new disk plus a DVD drive with `isoPath`, set as the first boot device. Windows setup ISOs show "Press any key to boot from CD or DVD" for a few seconds: open the console right after `start` and press a key, or the VM falls through to the empty disk.
- `existing_vhd`: attaches `vhdPath`; refused when another VM uses that file or it is a differencing parent.
- `linked`: a differencing disk over `parentPath`, which is then marked read-only; refused when a VM attaches the parent.
- `none`: no disk.

Memory is set static (or dynamic with the given bounds) explicitly, and a Generation 2 VM boots from its disk before its network adapter (from the DVD first for `iso`), because New-VM alone enables dynamic memory and boots the network first.

`osFamily` sets the guest defaults: `windows` gets Secure Boot with the MicrosoftWindows template and a vTPM; `linux` gets Secure Boot with the MicrosoftUEFICertificateAuthority template and the HvSocket enhanced-session transport; `other` turns Secure Boot off. A vTPM uses a local key protector, so the VM starts only on hosts holding this host's Shielded VM guardian certificates.

Creation refuses an existing VM name (unless `allowDuplicateName`), an existing target disk file and a disk larger than the volume's free space, before any change (a differencing disk needs only 1 GB free, and the result warns when the volume could not hold the parent's full size). If a later step fails, the half-created VM and its new disks are removed and the original error is returned. Read `warnings` and `nextSteps` in the result. An image VM returns `{ vm, disk, parent, started, warnings }` (or the job, when it fetched first); its `warnings` include the image's, such as the evaluation notice: run `slmgr /xpr` in the guest for the evaluation's end date, checkpoint after the first boot, and recreate the VM from the image when it expires.

## Configure, firmware, media and network

- `hyperv_vm_configure` changes CPU, memory (with dynamic memory bounds), checkpoint settings, notes, automatic start/stop actions, nested virtualization, integration services (by English name; matching is locale-independent) and the session transport; it returns the refreshed VM.
- `hyperv_firmware` (Generation 2, Off) applies the Secure Boot template first, because it can no longer change once the vTPM has been initialized, then Secure Boot, then the key protector (created when missing) and vTPM, then the first boot device; it returns firmware and security read back. Disabling the vTPM makes secrets the guest sealed to it (BitLocker, Windows Hello) unavailable. Change Secure Boot only for the guest task at hand.
- `hyperv_disk` attaches, detaches, inserts, ejects and resizes media; it never deletes files. Disk attach refuses files another VM uses and differencing parents; resize refuses checkpoint files and shared parents.
- `hyperv_network` lists and creates switches (Private, Internal, or NAT: an Internal switch whose host adapter takes the subnet's first address, plus a WinNAT network; WinNAT has no DHCP, so guests need static addresses), removes a switch with its same-named NAT network, and adds, removes, connects and configures VM adapters. Use a unique `adapterName` per VM. Switch removal can affect other VMs; inspect connections first. External switches are created through host administration, not here.

## Virtual disks

`hyperv_vhd` creates dynamic, fixed or differencing disks (never overwriting a file), inspects a disk's parent chain, the VMs that attach it and the disks it is a parent of, and deletes a disk only when no VM attaches it (checkpoints included), no other disk uses it as a parent and the host has not mounted it. Use `dryRun=true` to see whether a delete would be refused and why. Never delete or modify a differencing parent by other means: its children break. Never delete `.avhdx` checkpoint files by hand; remove checkpoints instead.

## Clone

`hyperv_vm_clone` needs the source Off. `mode=linked` creates a differencing disk over each of the source's current disks and a new VM with the source's generation, memory, vCPUs, Secure Boot, checkpoint settings and adapters (new MACs) and, when the source has one, a new vTPM. The source disks become read-only parents: the source can no longer start, and changing them breaks every linked clone. Sources with checkpoints are refused. `mode=full` exports and imports a copy with a new ID into `<destination>\<newName>`, with new MACs and disk identifiers; it copies every disk and can take minutes. Both copy the guest installation (computer name, SID; a full clone also its vTPM), so generalize the source with sysprep when clones must coexist on a network or domain. `hyperv_vm_export` plus `hyperv_vm_import` remain available for exports kept elsewhere.

## Remove

`hyperv_vm_remove` takes an Off or Saved VM; it never powers a VM off. Run it with `dryRun=true` first and read the plan. Without `deleteDisks`, Hyper-V deletes only the VM's configuration and runtime state (including saved state and vTPM state); every disk file stays, and checkpoint `.avhdx` files are merged into their parents in the background after removal. With `deleteDisks`, the tool removes the VM's checkpoints first and waits for their merge (a Saved VM with checkpoints is refused: turn it off first), then removes the VM, then deletes its disk files except those another VM attaches, differencing parents of other disks (a linked clone's template stays) and ISOs; every deletion is verified, and the VM's own folder is removed when nothing is left in it. The result lists what was deleted and what was kept, with reasons.

## Power and checkpoints

Use graceful `shutdown` or `restart` for ordinary work. `restart` waits for Off before Start. `turn_off` and `reset` are hard power operations, used only when the task calls for them. No automatic fallback from graceful to hard power. Pause/resume and save are distinct operations. Reopen a console after a power transition or checkpoint restore if the transport disconnected.

Before experiments that require rollback, record the current VM state and checkpoint IDs, then create a named task checkpoint. Restore/delete by ID. `current` means the VM's parent checkpoint, not the newest timestamp anywhere in its branching tree. Restoring replaces guest state and discards subsequent changes; save needed output to the host first. Delete only the task's checkpoints unless broader cleanup was requested. At the end, verify the intended power state and checkpoint tree.
