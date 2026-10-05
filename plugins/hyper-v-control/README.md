# Hyper-V Control

Manage a Hyper-V lab from your AI agent. Create and restore experiments, interact with guest desktops, move files, and investigate Windows applications or the kernel through one MCP connection.

## What it helps with

- **VM management:** create, configure, start, stop, clone, import, and export virtual machines; manage disks and networking.
- **Repeatable experiments:** inspect checkpoint trees, save a known state, and restore it after testing.
- **Desktop interaction:** use Basic or Enhanced Session, capture screenshots, send mouse and keyboard input, and work with guest UI Automation.
- **Guest automation:** run commands and processes, transfer files and folders, and manage reusable credentials.
- **Debugging:** use persistent application and kernel debugger sessions, including KDNET and named-pipe connections.

## Install and connect

Requires a Windows x64 host with Hyper-V. The distributed executable is self-contained and needs no separate .NET or Python installation.

Install **Hyper-V Control** from **Jerry's Plugin Marketplace** in Codex or Claude Code; see the [marketplace setup](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/blob/main/README.md#install). The plugin includes its skill and launcher. On first start, the launcher downloads and verifies the required executable automatically; subsequent starts reuse the installed runtime. Invoke `$hyper-v-control` in Codex or `/hyper-v-control:hyper-v-control` in Claude Code, or describe a VM task.

For another stdio MCP client, download the EXE from the [runtime releases](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/blob/main/docs/guides/releases.md#release-index) and point its configuration at the executable's absolute path. This example assumes you saved it as `C:\Tools\HyperVControl.exe`:

```json
{
  "mcpServers": {
    "hyper-v-control": {
      "command": "C:\\Tools\\HyperVControl.exe",
      "args": ["--elevate"]
    }
  }
}
```

For an offline machine, use the plugin's `scripts/install.ps1` in your preparation workflow or copy the matching, verified executable into the plugin's `bin` directory before starting it.

## Permissions and guest setup

With `--elevate`, Windows requests administrator approval once when needed. Subsequent operations use the same elevated worker until the MCP session ends. The MCP client can stay unelevated. UAC cancellation exits without operating on VMs; alternate-user elevation is unsupported. See the [permission model](skills/hyper-v-control/references/runtime.md).

Run `hyperv_capabilities` to inspect the host and available integrations. Windows guest automation needs credentials for PowerShell Direct. UI Automation needs an unlocked interactive guest desktop; Enhanced Session needs guest support and host policy. Basic Session can interact with boot screens and guests without PowerShell Direct.

Install Microsoft WinDbg separately for debugging. Its modern debugger engine is preferred; `HYPERV_CONTROL_DEBUGGER_DIR` can select an explicit debugger directory. See the [agent workflow](skills/hyper-v-control/SKILL.md) for task-specific setup and checkpoint guidance.

## Development and validation

Build and test from the repository root using the SDK specified by the [release guide](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/blob/main/docs/guides/releases.md):

```powershell
./scripts/build-hyperv-control.ps1
dotnet run --project tests/HyperVControl.Tests -c Release -- unit .
dotnet run --project tests/HyperVControl.Tests -c Release -- smoke .
```

[Feature inventory](docs/extraction.md) · [Validation records and known limits](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/blob/main/docs/README.md#validation-records) · [Third-party notices](THIRD-PARTY-NOTICES.md)
