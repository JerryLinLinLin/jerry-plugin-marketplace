# Jerry's Plugin Marketplace

**Windows reverse engineering and automation for AI agents.**

Understand unfamiliar software, investigate live behavior, make verifiable changes, and run repeatable experiments. Each plugin pairs specialist tools with practical workflows for your agent.

## Plugins

| Plugin | What it helps you do |
| --- | --- |
| [Rizin RE Toolkit](plugins/rizin-re-toolkit) | Understand executables and memory dumps through disassembly, multiple decompilers, signatures, and pattern matching. |
| [Frida Use](plugins/frida-use) | Observe running programs, trace API calls, inspect runtime values, and test reversible behavior changes. |
| [Capstone Binary Patching](plugins/capstone-binary-patching) | Compare instructions, create precise binary patches, verify the result, and roll changes back. |
| [Hyper-V Control](plugins/hyper-v-control) | Manage VMs and checkpoints, interact with guest desktops, transfer files, and debug applications or the Windows kernel. |

Use them independently or together: understand a program, observe it running, verify a change, and repeat the experiment from a known VM state.

## Install

### Codex

```powershell
codex plugin marketplace add JerryLinLinLin/jerry-plugin-marketplace --ref main
```

Open **Jerry's Plugin Marketplace** in Codex and install the plugins you need. Follow each plugin's setup guide, then describe your task to the agent.

### Claude Code

Add the marketplace, then run the install commands for the plugins you need:

```powershell
claude plugin marketplace add JerryLinLinLin/jerry-plugin-marketplace
claude plugin install rizin-re-toolkit@my-plugin-marketplace
claude plugin install frida-use@my-plugin-marketplace
claude plugin install capstone-binary-patching@my-plugin-marketplace
claude plugin install hyper-v-control@my-plugin-marketplace
```

In Claude Code, invoke a skill as `/<plugin-id>:<skill-id>`, for example `/rizin-re-toolkit:rizin-re-toolkit`, or describe a relevant task. Follow the plugin's setup guide for its runtime dependencies. Hyper-V Control requires a Windows x64 host with Hyper-V; its MCP launcher handles runtime setup on first start.

For local validation and development, see the [contribution guide](CONTRIBUTING.md#check-a-change).

## Releases

[Releases](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases)

## Contributing

[Contribution guide](CONTRIBUTING.md)

## Release guidelines

[Release guide](docs/guides/releases.md)

## License

[MIT](LICENSE)
