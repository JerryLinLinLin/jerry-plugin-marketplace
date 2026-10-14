# Validation

Validation date: October 14, 2026 (America/Chicago). Local Windows 11 Pro 26200 x64 host with client Hyper-V (VM configuration version 12.0), .NET SDK 10.0.401, self-contained .NET runtime 10.0.12, official MCP C# SDK 2.2.0. Runtime 0.2.0 adds VM lifecycle v2 (state-gated configuration, create sources, guarded removal, clones, standalone disks, adapters, NAT switches), an image catalog and library with background jobs, golden image builds, and a shared PowerShell script pipeline with a mock-module test seam.

Live phases ran on throwaway VMs named `HyperVControl-validation-*` with files under `D:\HyperVControl-validation\`, all removed afterwards. The user's existing VMs (`Windows 11 AV Test`, `sibyl-test01`, `New Virtual Machine`) and switches were never changed. The user authorized the throwaway VMs, one download of the Ubuntu 22.04 gallery image, and a user-local .NET SDK 10 installation.

## Measured behavior

| Area | Evidence |
| --- | --- |
| Build | C# compilation and single-file publish with zero warnings/errors; `--version` reports 0.2.0 |
| Unit phase | 192 checks pass against the recording mock module (no Hyper-V host): script quoting, guest wrapper, lifecycle v1/v2 state rules and parameter rendering, image catalog parsing of the real feed, fetch/resume/hash/cancel over a local HTTP server, deploy rendering, image build scripts and unattend, elevation bridge header |
| MCP protocol | Official client initialized the EXE over stdio and discovered 41 unique tools; invalid paths and unknown actions return the uniform `{ok:false, error, errorType}` envelope |
| Elevation | Same-user worker; `HYPERV_CONTROL_IMAGE_ROOT` set only in the client process reached the elevated worker (the worker reported the library root under that path) |
| Lifecycle v2 (`lifecycle-live`) | 28 of 28 steps: create (new disk), configure Off, start, configure while Running (static memory resize attempted; vCPU change refused with the setting named), adapter add/remove while Running, firmware/TPM, standalone VHD create/inspect, linked clone, remove with dry run then `deleteDisks`, files verified gone |
| Images (`images-live`) | Catalog list flagged the expired Windows 11 image and recommended Ubuntu 22.04; fetch job downloaded 2,484,769,532 bytes, verified SHA-256, extracted a 6,417,285,120-byte Zip64 VHDX in 172 s and deleted the archive; linked-clone deploy on the Default Switch (resolved by ID) with Secure Boot off per the feed, HvSocket, static memory, Standard checkpoints; the VM booted (heartbeat OK, basic console showed Ubuntu's first-boot wizard); remove refused while the clone existed; VM, disk, entry and file removed |
| Image build (`image-build-live`) | Synthetic WIM inside an ISO through the full Windows pipeline in 26 s: GPT layout (300 MB FAT32 EFI, 16 MB MSR, NTFS), applied files, Panther unattend with the exact password, byte-identical `C:\Tools`, boot store with debug and network-debug settings; a WIM without boot files failed with bcdboot's reason and its outputs were removed; Linux build refused without `qemu-img` naming `HYPERV_CONTROL_QEMU_IMG`; a `.vhdx` source was resized and a `CIDATA` seed ISO with `user-data` and `meta-data` mounted on the host |
| Integration (final build) | 19 of 19 steps: `hyperv_vhd` create, `hyperv_image` register, `hyperv_vm_create source=image` (linked clone of the registered template, parent read-only), info, start, Running-state refusal, full clone as a job, export as a job, jobs list, remove dry run, remove with `deleteDisks` for both VMs, image remove with file deletion, inventory unchanged |
| Removal reporting | A linked VM's removal lists its parent under `kept` with the reason and the `hyperv_vhd delete` follow-up; the parent was then deleted once unused |
| Packaging | Marketplace checker (49 documents), runtime packaging, launcher test from a path with spaces, release isolation and corrupt-download rejection all pass |

## Findings fixed during validation

- `Export-VM` fails with `Access is denied (0x80070005)` when any disk in the VM's chain carries the read-only attribute, which the plugin sets on linked parents. Reproduced with a plain VM (export succeeded), a linked VM (failed), and the same VM after clearing the attribute (succeeded). Exports and full clones now clear the attribute for the duration of the export and restore it.
- A UAC launch starts the worker with the profile environment, so every `HYPERV_CONTROL_*` override set only in the MCP client's process environment was lost. The bridge now forwards `HYPERV_CONTROL_*`, `HYPERV_GUEST_*` and `_NT_SYMBOL_PATH` over its pipe.
- `New-VM` on this host enables dynamic memory and boots from the network adapter before the disk; create and linked clone set the memory mode and first boot device explicitly.
- A full clone's copied parent and a linked clone's template were neither deleted nor reported by removal; they are now listed under `kept`.

## Follow-up on October 14, 2026, before the release

Three changes landed after the runs above, on the same host. The executable was rebuilt and `runtime.json` re-pinned; the 0.2.0 asset is still unpublished. The Images row above records the date heuristic that flagged the Windows 11 image as expired; that heuristic was wrong (the user verified that Quick Create still downloads and runs the image) and is gone.

| Area | Evidence |
| --- | --- |
| Elevation on demand | `--elevate` no longer prompts at start. Through the real launcher, unelevated: initialize, 41 tools and ping were answered with no worker process. With the test seam `HYPERV_CONTROL_WORKER=always` on the published EXE: the first tool call starts exactly one worker, ten concurrent first calls start one worker, the second call reuses it, `HYPERV_CONTROL_IMAGE_ROOT` set only for the server reaches the worker, closing stdin ends both processes within about 40 ms, and a worker killed between calls is reported once as `ElevatedWorkerExited` and replaced on the next call. Unit checks with injected launchers cover the `runas` start info, `ElevationDeclined` with a new prompt on the next call, cancellation while a prompt is open, and the refusal of a non-elevated worker without the seam. |
| Default image | `hyperv_vm_create` defaults to `source=image` with Microsoft's Windows 11 dev environment, or `HYPERV_CONTROL_DEFAULT_IMAGE`. Unit checks: the saved gallery feed marks the image fetchable and recommended with an evaluation warning; default resolution with and without the variable; a library image deploys within the call against the mock module; an absent image runs one quick-create job (local HTTP fixture, then the mock module) whose result is the VM; a running fetch of the same image is joined; parameters of other sources are refused. The smoke phase confirms both refusals on the published EXE before any download. No image was downloaded. |
| Bundle size | `EnableCompressionInSingleFile`: 53,380,027 bytes instead of 120,887,421. Measured here: `--version` 79 ms (42 ms uncompressed) and initialize plus tools/list 258 ms (210 ms). ReadyToRun gave 146 MB and 109 ms but no gain once compressed; Native AOT is refused (`PublishTrimmed` cannot be false for Windows Forms, and 89 IL2026/IL3050 warnings come from WMI, the RDP ActiveX control and reflection-based JSON). Per tool call the host still pays about 1 s for powershell.exe plus the Hyper-V module import. |
| Differencing disks | CI's small runner volume exposed that a linked clone or a differencing `hyperv_vhd` demanded the parent's full virtual size free on the target volume (the mock parent reports 64 GB; the runner had about 14 GB), which would also refuse a linked clone of the 44.8 GB Windows 11 image on most lab volumes. A differencing child now needs 1 GB free and the result warns when the volume could not hold the parent's size. |
| Checks | Unit 215 (192 before), stdio smoke including the proxy checks, launcher, release isolation, runtime packaging, marketplace checker (50 documents), and the CI comparison on two compressed builds that differ only in build identifiers. |

## Practical limits

- The real UAC prompt was not clicked in this session: approving, declining, and the elevated worker's process and user checks under a high-integrity token were exercised only through the seam. The first tool call of the next session is the test.
- The Windows 11 dev environment was not fetched or booted through the plugin (23.3 GB download, about 69 GB free needed); Quick Create's own download and VM were verified by hand. The plugin's deploy of that image, with Secure Boot and a vTPM, is covered by the mock module only.
- A static memory resize on a Running VM is refused by Hyper-V until a guest OS has booted; the live VMs had no OS, so a successful live resize was not observed.
- No VM has booted from a built golden image. On this host `bcdboot` could not create the boot store with any source, so the pipeline falls back to `bcdedit` with the image's `BCD-Template` and says so in the warnings; unattend behavior on 26H2 is unverified. The evaluation ISO download, `qemu-img` conversion and cloud-init in a real Ubuntu VM were tested only against mocks and local servers.
- NAT switch creation, removing a Saved VM, Generation 1 paths, attaching an ISO while Running, full clones of VMs with checkpoints, file and UNC catalogs, `keepArchive`, resume over the internet and the Windows deploy path (vTPM, Microsoft Windows template) were exercised only against the mock module.
- Generation 2 guests detect a COM port mapped while Running only after a full stop and start.
- During an early experiment of the image-build work, `bcdboot` was run once against the host's own `C:\Windows`; BitLocker logged a key reseal on the system volume. The boot manager was found byte-identical afterwards and protection stayed on. The shipped pipeline never uses the host's Windows as a source.

## Reproduction

Build with `scripts/build-hyperv-control.ps1 -DotNet <SDK 10 dotnet.exe>`; run the C# harness in `unit` and `smoke` modes, then `scripts/test-hyperv-launcher.ps1` and `scripts/test-release-isolation.ps1`. Live phases are explicit: `lifecycle-live`, `images-live` (downloads 2.5 GB; set `HYPERV_CONTROL_IMAGE_ROOT` to a volume with 10 GB free), `image-build-live`, plus the earlier `prepare`/`guest`/`ui`/`debug`/`kernel-*`/`cleanup` phases for the unchanged channels. Each live phase creates and removes its own `HyperVControl-validation-*` VMs and needs explicit authorization. Raw results are written under ignored `build/hyperv-control/validation`.
