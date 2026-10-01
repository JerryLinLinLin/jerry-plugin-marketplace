# My Plugin Marketplace

A personal Codex plugin marketplace. Each plugin lives in its own folder, so this repository can grow beyond the original Rizin bundle.

| Plugin | What it provides |
| --- | --- |
| [Rizin Windows RE](plugins/rizin-windows-re) | A portable Windows x64 Rizin CLI, RetDec/Ghidra/jsdec decompilers, YARA, FLIRT, and a skill for native binary and memory-dump analysis. |

## Add to Codex

```powershell
codex plugin marketplace add JerryLinLinLin/rizin-win64-bundle --ref main
```

Open the plugin directory, select **My Plugin Marketplace**, and install **Rizin Windows RE**. A normal marketplace clone does not need the build-source submodules. The CLI is downloaded separately from GitHub Releases when the skill needs it.

The catalog is [.agents/plugins/marketplace.json](.agents/plugins/marketplace.json), following the [official OpenAI plugin marketplace format](https://developers.openai.com/plugins/build/plugins). Plugin paths are relative to the repository root.

## Portable Rizin download

Get `rizin-windows-x64-bundle-v0.3.0.zip` and `SHA256SUMS` from the [Rizin release](https://github.com/JerryLinLinLin/rizin-win64-bundle/releases/tag/rizin-v0.3.0). Verify SHA-256, extract the complete `rizin` directory, and run:

```powershell
& 'C:\path\to\rizin\bin\rizin.exe' -v
```

Windows 10 (1903+) or Windows 11 x64 is required. Runtime DLLs are included beside the executables; no administrator installation or system PATH change is needed. Move the whole folder together. Paths containing spaces are supported.

For a user-level install, run the plugin's [download helper](plugins/rizin-windows-re/skills/rizin-windows-re/scripts/install.ps1). It selects a stable Rizin asset, verifies its checksum, and extracts to `%LOCALAPPDATA%\Programs\Rizin\<tag>`. `-AddToUserPath` optionally adds its `bin` directory to the user's PATH.

## Add another plugin

1. Create `plugins/<plugin-name>/plugin.json` and `skills/<skill-name>/SKILL.md` within that folder. Keep referenced assets and scripts inside the plugin.
2. Add its entry to `.agents/plugins/marketplace.json`, using `source.path: ./plugins/<plugin-name>`, a category, and installation/authentication policies.
3. Bump the plugin's semantic version when shipping changes. Keep large CLI binaries in versioned GitHub release assets.
4. Validate the manifest and skill, then test discovery with Codex.

Rizin includes a portable Agent Plugins manifest and a `.codex-plugin/plugin.json` compatibility manifest. It is a CLI-and-skill plugin and needs no MCP server or account connection.

## Build and verification

See [the build guide](docs/build.md), [component lock file](bundle.lock.json), and [release validation](docs/validation-v0.3.0.md). Build sources are pinned under `sources/`; Windows compatibility patches live under `patches/`. Generated files stay in the ignored `build/` directory.

## Repository naming

The GitHub repository name is unchanged. Possible future names: `my-plugin-marketplace`, `jerry-codex-plugins`, or `jerry-agent-toolbox`. After renaming, update repository URLs in the plugin manifests, installer default, lock file, and documentation. Plugin identity and marketplace-relative paths can stay the same.

## License

Marketplace code and skill: [MIT](LICENSE). Runtime components retain their upstream licenses. The bundle includes component notices and source revision information; MIT does not replace upstream terms.
