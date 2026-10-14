---
name: hyper-v-control
description: Manage Hyper-V VM lifecycle and checkpoints, operate Basic or Enhanced guest consoles, transfer files, automate Windows guest UI, and debug user processes or guest kernels using the Hyper-V Control MCP. Use for Hyper-V VM operations and Windows VM debugging, not general host desktop automation.
---

# Hyper-V Control

Use this plugin's `hyperv_*` MCP tools. The runtime is a compiled Windows x64 C# EXE speaking stdio. It requires no Python or user-side SDK. If unavailable, read [runtime and permissions](references/runtime.md). The first tool call of a session can show a Windows UAC prompt; after `ElevationDeclined`, ask the user before calling again, because each new call prompts again.

Start with `hyperv_capabilities` and `hyperv_list` when host or target state is unknown. Resolve the exact VM name/ID and re-read state before dependent operations. An existing authorization to operate a test VM carries through the requested workflow; ask only for missing credentials or materially new/destructive scope.

Choose the channel that fits the task:

| Task | Channel |
| --- | --- |
| Inventory, creation, hardware, start/stop, export/import | VM tools; no console needed. `vm_create` defaults to `source=image` (the default image, next rows); other sources: new_disk, iso, existing_vhd, linked, none. Changes are gated by VM state and refused with the list of settings that need Off |
| Delete a VM safely, inspect or create disks, clone | `vm_remove` with `dryRun` then `deleteDisks` (never powers off; keeps shared disks and linked parents); `vhd` create/inspect/delete; `vm_clone` linked (seconds) or full (a job) |
| Obtain a bootable image: Microsoft's gallery or a custom catalog, the local library, verified download and extraction | `image` (`list` with `defaultImage`, `fetch` as a job, `register`, `remove`, `catalogs`); poll with `jobs` |
| Create a Windows lab VM in one call: the default image is Microsoft's Windows 11 dev environment (a Windows 11 Enterprise evaluation), or `HYPERV_CONTROL_DEFAULT_IMAGE` | `vm_create` without `source`: a linked clone with lab defaults in seconds once the image is in the library; otherwise one job downloads it first (23.3 GB, about 69 GB free needed) and its result is the VM. Before that first download, check `image` `list` (`defaultImage.inLibrary` is null) and confirm with the user. `image` `deploy` or `vm_create imageName=` for other images |
| Build a golden template without running Setup: Windows ISO or evaluation media applied offline with an unattend, or an Ubuntu cloud image with a NoCloud seed | `image_build` (a job); register the sidecar with `image` `register` |
| Anything that outlives one call: downloads, extraction, export/import, full clones, image builds | `jobs` (`list`, `status`, `wait`, `cancel`) |
| Reproduce and roll back a test | Checkpoint tools; retain the checkpoint ID and initial power state |
| Boot, firmware, sign-in, locked desktop, visual clicks | Basic console + inline screenshot + input |
| Resizable remote desktop and clipboard | Enhanced console; check the actual mode returned |
| Guest scripts, installers, files, process diagnostics | PowerShell Direct (`guest_run`, `guest_process`, `copy`) |
| Find controls by name, inspect elements, invoke/set value | `guest_setup` then `ui` in an unlocked interactive guest session |
| Debug a process inside the VM | Guest debug worker; guest PIDs belong in `guest_debug_open` |
| Debug the VM's kernel | KDNET or two-phase KDCOM, then host KD session |
| Analyze an existing dump | `debug_open` with `mode=dump`; copy guest dumps to host first |

For lifecycle/rollback details read [VM operations](references/operations.md). For catalogs, the image library, fetch jobs and deployment read [images](references/images.md). For golden image builds read [image builds](references/image-build.md). For console, keyboard/mouse, clipboard and winapp read [guest interaction](references/interaction.md). For live debugging read [debugging](references/debugging.md).

Read the actual MCP schema for parameter names. Tool failures arrive as `isError` with an explanation; do not interpret process startup, BCD configuration or a timeout as successful attachment/execution. After a mutation, verify through the channel that observes its result. Report unsupported states rather than substituting host input or changing guest security settings silently.
