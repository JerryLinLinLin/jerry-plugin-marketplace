# Hyper-V Control

A Windows x64 C# MCP server over standard input/output for Hyper-V lifecycle, checkpoints, graphical guest control, file transfer and user/kernel debugging. The distributed `HyperVControl.exe` is self-contained: end users need neither Python nor a .NET SDK/runtime installation.

## Run

Get the [Windows x64 EXE](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/download/hyper-v-control-v0.1.0/hyper-v-control-win-x64-v0.1.0.exe) or [complete plugin ZIP](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/download/hyper-v-control-v0.1.0/hyper-v-control-plugin-v0.1.0-win-x64.zip) from [release 0.1.0](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/tag/hyper-v-control-v0.1.0). The release includes a namespaced checksum file and machine-readable asset inventory. It shares no release tag or installation directory with Rizin.

Extract the prebuilt plugin ZIP and install this plugin from the marketplace/local plugin directory. It contains `bin/HyperVControl.exe`, manifests, launcher and `$hyper-v-control` skill. A source-only marketplace installation can run `scripts/install.ps1` after the matching release has been published; downloads are pinned by SHA-256 in `runtime.json`.

For any stdio MCP client:

```json
{
  "mcpServers": {
    "hyper-v-control": {
      "command": "C:\\Tools\\HyperVControl\\hyper-v-control-win-x64-v0.1.0.exe",
      "args": ["--elevate"]
    }
  }
}
```

`--elevate` keeps the parent connected to the MCP client and opens a same-user elevated worker over a protected named pipe. The client itself need not be elevated. With UAC enabled, Windows may request consent; cancellation exits without operating on VMs. With sufficient existing privileges, the EXE can run without this option. Alternate-user UAC credentials are intentionally rejected by the same-user boundary. See the [permission model](skills/hyper-v-control/references/runtime.md).

## Capabilities

- VM inventory, create/configure/remove, power states, import/export/clone, virtual switches and adapters, VHD/ISO operations, Secure Boot.
- Checkpoint tree/current-parent discovery, create, restore, rename and delete.
- Basic and Enhanced Session consoles using Hyper-V RDP ActiveX; native framebuffer screenshots in Basic, guarded screen capture in Enhanced; keyboard/chords/text, pointer/buttons/scroll/drag, Enhanced clipboard.
- Credential Manager profiles; PowerShell Direct commands and processes with exit codes, timeouts and separate output streams; bidirectional files and folders, directory listing and bounded binary reads.
- Microsoft's winapp guest UI Automation through an interactive scheduled task.
- KDNET and two-phase named-pipe KDCOM configuration with checked `bcdedit` results.
- Persistent KD/CDB sessions: commands, break, poll, detach; symbols, memory, registers, stacks, breakpoints, stepping and dump analysis through normal debugger commands.
- Persistent in-guest CDB workers over PowerShell Direct, without guest networking or a second MCP server.

Use `hyperv_capabilities` to inspect the actual environment. Windows guests need credentials for PowerShell Direct; semantic UI also needs an unlocked interactive desktop. Basic console can control boot and non-Windows guests without PowerShell Direct. Enhanced requires guest support and host policy. Host management is local; the graphical guest sessions are remote through Hyper-V, not through an HTTP MCP listener.

Current Microsoft WinDbg is preferred. Set `HYPERV_CONTROL_DEBUGGER_DIR` to the complete `amd64` directory to select an engine explicitly. Modern MSIX packages are discovered ahead of legacy Windows Kits. The EXE returns the path and version used. Downloading the MCP EXE does not implicitly install WinDbg or alter Secure Boot, BitLocker, firewall or UAC policy.

## Build and validate

From the repository root with .NET 10 SDK:

```powershell
./scripts/build-hyperv-control.ps1
dotnet run --project tests/HyperVControl.Tests -c Release -- unit .
dotnet run --project tests/HyperVControl.Tests -c Release -- smoke .
```

The build creates the namespaced EXE, complete plugin ZIP, `hyper-v-control-v0.1.0-release.json` and `hyper-v-control-v0.1.0-SHA256SUMS.txt` under `build/hyperv-control/release`. Local `HyperVControl.exe` and `SHA256SUMS` aliases are for development only. Source lives under `src/HyperVControl`, independent of the Rizin plugin. Runtime binaries/build caches remain outside Git. The [extraction inventory](docs/extraction.md) maps RaivenX and reference-MCP features to their implementations. The [validation report](docs/validation.md) distinguishes measured results from untested environments.

See [source/runtime notices](THIRD-PARTY-NOTICES.md). Release publication is separate from building local artifacts.
