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

```powershell
codex plugin marketplace add JerryLinLinLin/jerry-plugin-marketplace --ref main
```

Open **Jerry's Plugin Marketplace** in Codex and install the plugins you need. Follow each plugin's setup guide, then describe your task to the agent.

## Releases

[Releases](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases)

## Contributing

[Contribution guide](CONTRIBUTING.md)

## Release guidelines

[Release guide](docs/guides/releases.md)

## License

[MIT](LICENSE)
