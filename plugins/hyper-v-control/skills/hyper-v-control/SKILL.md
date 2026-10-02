---
name: hyper-v-control
description: Manage Hyper-V VM lifecycle and checkpoints, operate Basic or Enhanced guest consoles, transfer files, automate Windows guest UI, and debug user processes or guest kernels using the Hyper-V Control MCP. Use for Hyper-V VM operations and Windows VM debugging, not general host desktop automation.
---

# Hyper-V Control

Use this plugin's `hyperv_*` MCP tools. The runtime is a compiled Windows x64 C# EXE speaking stdio. It requires no Python or user-side SDK. If unavailable, read [runtime and permissions](references/runtime.md).

Start with `hyperv_capabilities` and `hyperv_list` when host or target state is unknown. Resolve the exact VM name/ID and re-read state before dependent operations. An existing authorization to operate a test VM carries through the requested workflow; ask only for missing credentials or materially new/destructive scope.

Choose the channel that fits the task:

| Task | Channel |
| --- | --- |
| Inventory, creation, hardware, start/stop, export/import | VM tools; no console needed |
| Reproduce and roll back a test | Checkpoint tools; retain the checkpoint ID and initial power state |
| Boot, firmware, sign-in, locked desktop, visual clicks | Basic console + inline screenshot + input |
| Resizable remote desktop and clipboard | Enhanced console; check the actual mode returned |
| Guest scripts, installers, files, process diagnostics | PowerShell Direct (`guest_run`, `guest_process`, `copy`) |
| Find controls by name, inspect elements, invoke/set value | `guest_setup` then `ui` in an unlocked interactive guest session |
| Debug a process inside the VM | Guest debug worker; guest PIDs belong in `guest_debug_open` |
| Debug the VM's kernel | KDNET or two-phase KDCOM, then host KD session |
| Analyze an existing dump | `debug_open` with `mode=dump`; copy guest dumps to host first |

For lifecycle/rollback details read [VM operations](references/operations.md). For console, keyboard/mouse, clipboard and winapp read [guest interaction](references/interaction.md). For live debugging read [debugging](references/debugging.md).

Read the actual MCP schema for parameter names. Tool failures arrive as `isError` with an explanation; do not interpret process startup, BCD configuration or a timeout as successful attachment/execution. After a mutation, verify through the channel that observes its result. Report unsupported states rather than substituting host input or changing guest security settings silently.
