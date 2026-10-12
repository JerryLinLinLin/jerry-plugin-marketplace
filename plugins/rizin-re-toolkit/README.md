# Rizin RE Toolkit

Understand native executables and memory dumps with a portable Windows analysis toolkit. Rizin brings disassembly, multiple decompilers, YARA matching, and FLIRT signatures into a workflow your agent can use from the command line.

## What it helps with

- Triage unfamiliar binaries and find relevant functions, strings, imports, and cross-references.
- Compare Ghidra, RetDec, and jsdec output when one decompiler leaves questions open.
- Investigate captured memory, loaded modules, and code at meaningful addresses.
- Identify known code and patterns with signatures and YARA rules.

## Install

Install **Rizin RE Toolkit** from **Jerry's Plugin Marketplace** in Codex or Claude Code; see the [marketplace setup](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/blob/main/README.md#install). The included skill guides runtime setup and analysis. Invoke `$rizin-re-toolkit` in Codex, `/rizin-re-toolkit:rizin-re-toolkit` in Claude Code, or describe a relevant task.

The skill first checks a known installation path, then `%LOCALAPPDATA%\Programs\Rizin\<release-tag>\rizin\bin\rizin.exe`, then PATH, and reuses a working runtime before downloading. The [PowerShell installer](skills/rizin-re-toolkit/scripts/install.ps1) downloads and verifies the runtime when needed. By default it adds `bin` only to the current PowerShell process's PATH; use `-AddToUserPath` for persistent user PATH registration or `-Destination <directory>` for a custom installation root. Across separate shell sessions, use the resolved absolute executable path.

For manual installation, use the [download catalog](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/blob/main/docs/guides/releases.md#release-index), verify the archive checksum, and extract the complete `rizin` folder. Run `rizin\bin\rizin.exe` by absolute path. Keep the folder together when moving it; required runtime libraries and support data are included.

## Use

Ask your agent to explain a binary's behavior, compare decompiler output, or investigate a memory dump. For direct CLI use:

```powershell
rizin -q -N -e scr.color=0 -c iI sample.exe
rizin -A -q -N -e scr.color=0 -c 'pdz @ main' sample.exe
rizin -q -N -e scr.color=0 -c iI -c omlj -c il sample.dmp
```

Use a real function or address in place of `main`. Dump analysis depends on the memory actually captured; see the [memory-dump guide](skills/rizin-re-toolkit/references/memory-dumps.md).

This plugin uses Rizin and its bundled analysis engines. Tool-specific project files and live debugging requests may need another tool; the agent checks capabilities before choosing a workflow.

## More information

[Agent workflow](skills/rizin-re-toolkit/SKILL.md) · [Build guide](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/blob/main/docs/guides/rizin-build.md) · [Releases and component versions](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/blob/main/docs/guides/releases.md)

Plugin code and instructions use the MIT license. Runtime components retain their own licenses, included with the bundle.
