# Jerry's Plugin Marketplace

**Windows reverse engineering and automation for AI agents.**

A collection of focused plugins for understanding software, investigating live behavior, making verifiable changes, and running repeatable experiments. Each plugin combines specialist tools with guidance that helps your agent choose a useful workflow and interpret the results.

## The plugins

### [Rizin RE Toolkit](plugins/rizin-re-toolkit)

Explore native executables and memory dumps without starting from scratch. Disassembly, multiple decompilers, signatures, and pattern matching help you understand unfamiliar code and cross-check what you find. A good starting point for binary triage, program analysis, and investigating captured memory.

### [Frida Use](plugins/frida-use)

See what a program actually does while it runs. Trace API calls, inspect arguments and return values, and try reversible changes to live behavior. Use it to investigate a suspicious call, follow a runtime-only code path, or test a hypothesis from static analysis.

### [Capstone Binary Patching](plugins/capstone-binary-patching)

Turn a proposed binary change into a precise, verifiable patch. Compare bytes and instructions, construct patches, check the result, and roll changes back. Useful when you need to explain exactly what changed, port a patch between versions, or preserve the original file for comparison.

### [Hyper-V Control](plugins/hyper-v-control)

Give your agent control of a Windows VM lab: manage VM lifecycles and checkpoints, interact with guest desktops, transfer files, and debug applications or the Windows kernel. Checkpoints make experiments repeatable, while desktop interaction and debugging let you investigate the same VM from several angles.

Use any plugin on its own, or combine them: understand a binary with Rizin, inspect its behavior with Frida, verify a patch with Capstone, and use Hyper-V to repeat the experiment from a known state.

## Get started

```powershell
codex plugin marketplace add JerryLinLinLin/jerry-plugin-marketplace --ref main
```

In Codex, open **Jerry's Plugin Marketplace** and install the plugins you need. Each plugin includes its skill. Follow the linked setup guides above for runtime requirements, then describe the task to your agent.

## Downloads

The [download catalog](docs/releases.md#release-index) lists prebuilt Windows packages: a standalone EXE or complete plugin for Hyper-V Control, and a complete plugin plus portable runtime for Rizin RE Toolkit.

Frida Use and Capstone Binary Patching are available through the marketplace. Install only what you need; plugins have independent setup and releases.

[Contributing](CONTRIBUTING.md) · [Release guide](docs/releases.md) · [MIT license](LICENSE). Bundled third-party components retain their own licenses.
