# Jerry's Plugin Marketplace

A personal Codex plugin monorepo. Each plugin has its own identity, version, installation directory and release stream.

| Plugin | What it provides |
| --- | --- |
| [Rizin RE Toolkit](plugins/rizin-re-toolkit) | A portable Windows x64 Rizin CLI, RetDec/Ghidra/jsdec decompilers, YARA, FLIRT, and a skill for native binary and memory-dump analysis. |
| [Capstone Binary Patching](plugins/capstone-binary-patching) | A uv-managed Capstone/LIEF CLI and skill for binary patches, instruction diffs, patch porting, verification and rollback, with native Windows experiments. |
| [Frida Use](plugins/frida-use) | Windows-focused live instrumentation, Python/JavaScript debugging, API tracing, malware analysis, reverse engineering, and reversible runtime patching. |
| [Hyper-V Control](plugins/hyper-v-control) | A compiled C# stdio MCP EXE and skill for VM lifecycle, checkpoints, Basic/Enhanced computer use, guest files/UI, and user/kernel debugging. |

## Add to Codex

```powershell
codex plugin marketplace add JerryLinLinLin/jerry-plugin-marketplace --ref main
```

Open the plugin directory, select **Jerry's Plugin Marketplace**, and install **Rizin RE Toolkit**, **Capstone Binary Patching**, **Frida Use**, or **Hyper-V Control** as needed. A normal marketplace clone does not need the build-source submodules. Rizin and Hyper-V Control download their own verified runtimes from separate release streams. Frida Use bundles its own skill, scripts, and references; install the Frida runtime in your analysis environment as described in its README.

**Hyper-V Control:** plugin/skill ID `hyper-v-control`, invocation `$hyper-v-control`, version `0.1.0`. Its C# stdio MCP is distributed as a self-contained Windows x64 EXE. A complete plugin ZIP includes the EXE; for a marketplace source installation, run [its installer](plugins/hyper-v-control/scripts/install.ps1) before reconnecting the MCP. See [setup and validation](plugins/hyper-v-control/README.md).

**Capstone Binary Patching** is a separate skill-and-CLI plugin for patching work; see its
[setup and experiments](plugins/capstone-binary-patching). Its Python dependencies are managed by uv,
and it requires no MCP server or account connection.

**Frida Use:** plugin/skill ID `frida-use`, invocation `$frida-use`, version `1.0.0`. Its full source is vendored under `plugins/frida-use`; it has no dependency on the former standalone project. See its [research notes](plugins/frida-use/skills/frida-use/references/research-sources.md). Maintainer experiments and results live separately in [scripts/frida-use-experiments](scripts/frida-use-experiments) and [docs/frida-use-validation.md](docs/frida-use-validation.md).

**Rizin RE Toolkit:** plugin/skill ID `rizin-re-toolkit`, invocation `$rizin-re-toolkit`. Automatic skill selection is enabled. Native RE requests mentioning Ghidra, IDA/Hex-Rays, Binary Ninja, Cutter, radare2, and related tools route to the toolkit's equivalent CLI workflow by default. Ghidra requests use its bundled `pdg` engine. Native application projects, tool-specific development, and unsupported debugging workflows receive a capability check rather than a claim of compatibility.

**Upgrading from Rizin Windows RE:** refresh the marketplace (`codex plugin marketplace upgrade my-plugin-marketplace`), install **Rizin RE Toolkit**, and remove the old **Rizin Windows RE** installation if present. The old ID `rizin-windows-re` has been replaced; an installed copy is not automatically renamed. The marketplace ID remains `my-plugin-marketplace`.

The catalog is [.agents/plugins/marketplace.json](.agents/plugins/marketplace.json), following the [official OpenAI plugin marketplace format](https://developers.openai.com/plugins/build/plugins). Plugin paths are relative to the repository root.

## Releases and downloads

| Component | Release | What to download |
| --- | --- | --- |
| Hyper-V Control | [0.1.0](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/tag/hyper-v-control-v0.1.0) | Namespaced Windows x64 EXE or complete plugin ZIP |
| Rizin RE Toolkit | [0.4.1](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/tag/rizin-re-toolkit-v0.4.1) | Complete plugin ZIP with its skill and isolated, paginated runtime installer |
| Rizin portable runtime | [0.3.1](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/tag/rizin-v0.3.1) | Existing runtime bundle and its unchanged `SHA256SUMS` |

Use the [release organization guide](docs/releases.md) for tag/asset naming, per-plugin checksum pins, independent runtime versions and publication steps. Installers do not use the repository-wide Latest release to select a plugin. Existing Rizin URLs remain compatible.

## Portable Rizin download

Get `rizin-windows-x64-bundle-v0.3.1.zip` and `SHA256SUMS` from the [Rizin release](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/tag/rizin-v0.3.1). Verify SHA-256, extract the complete `rizin` directory, and run:

```powershell
& 'C:\path\to\rizin\bin\rizin.exe' -v
```

Windows 10 (1903+) or Windows 11 x64 is required. Runtime DLLs are included beside the executables; no administrator installation or system PATH change is needed. Move the whole folder together. Paths containing spaces are supported.

For a user-level install, run the plugin's [download helper](plugins/rizin-re-toolkit/skills/rizin-re-toolkit/scripts/install.ps1). It selects a stable Rizin asset, verifies its checksum, and extracts to `%LOCALAPPDATA%\Programs\Rizin\<tag>`. `-AddToUserPath` optionally adds its `bin` directory to the user's PATH.

## Add another plugin

Write all plugin content in English, including documentation, skill instructions,
metadata, script comments, and bundled references. Summarize sources in English
and use English link labels even when the original source is in another language.

1. Create `plugins/<plugin-name>/plugin.json` and `skills/<skill-name>/SKILL.md` within that folder. Keep referenced assets and scripts inside the plugin.
2. Add its entry to `.agents/plugins/marketplace.json`, using `source.path: ./plugins/<plugin-name>`, a category, and installation/authentication policies.
3. Bump the plugin's semantic version when shipping changes. Follow [the release conventions](docs/releases.md) for component tags, namespaced assets and isolated runtime installation. Keep large binaries in GitHub Releases.
4. Validate the manifest and skill, then test discovery with Codex.

Rizin includes a portable Agent Plugins manifest and a `.codex-plugin/plugin.json` compatibility manifest. It is a CLI-and-skill plugin and needs no MCP server or account connection.

## Build and verification

This is a plugin monorepo. Hyper-V Control's C# source is under `src/HyperVControl`, its protocol/live validation harness under `tests/HyperVControl.Tests`, and its distributable plugin under `plugins/hyper-v-control`. Build its self-contained EXE and plugin ZIP with `scripts/build-hyperv-control.ps1`; see [Hyper-V Control](plugins/hyper-v-control/README.md) for runtime setup and [feature extraction](plugins/hyper-v-control/docs/extraction.md).

See [the build guide](docs/build.md), [component lock file](bundle.lock.json), and [release validation](docs/validation-v0.3.1.md). Build sources are pinned under `sources/`; Windows compatibility patches live under `patches/`. Generated files stay in the ignored `build/` directory.

## License

Marketplace code and skill: [MIT](LICENSE). Runtime components retain their upstream licenses. The bundle includes component notices and source revision information; MIT does not replace upstream terms.
