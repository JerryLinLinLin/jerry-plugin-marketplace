# Releases and binary distribution

This repository is a marketplace of independently versioned plugins. A GitHub Release belongs to one plugin or one explicitly separate runtime stream; it is not a version of the whole marketplace. GitHub assets are a flat list, so filenames carry their own identity.

## Release index

| Stream | Version and tag | Downloads | Installation |
| --- | --- | --- | --- |
| Hyper-V Control | [0.1.0](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/tag/hyper-v-control-v0.1.0) | C# EXE and complete Windows x64 plugin ZIP | `%LOCALAPPDATA%\Programs\HyperVControl\0.1.0` or the ZIP's `bin` directory |
| Rizin RE Toolkit | [0.4.1](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/tag/rizin-re-toolkit-v0.4.1) | Complete plugin ZIP, including its skill | Codex plugin directory |
| Rizin portable runtime | [0.3.1](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/tag/rizin-v0.3.1) | Rizin 0.9.1 Windows x64 runtime bundle | `%LOCALAPPDATA%\Programs\Rizin\rizin-v0.3.1` |
| Capstone Binary Patching | Plugin `0.1.0`, marketplace source | Skill/scripts; dependencies installed by uv | Its uv-managed environment; no repository binary release required |
| Frida Use | Plugin `1.0.0`, marketplace source | Skill/scripts/references; Frida installed separately | The selected analysis environment; no repository binary release required |

The marketplace's source of truth for plugin identity/version remains `plugins/<id>/plugin.json`. A compiled plugin additionally owns its runtime pin, installer, launcher and release builder. Do not couple unrelated plugin versions.

## Naming rules for new releases

| Item | Pattern | Hyper-V example |
| --- | --- | --- |
| Git tag | `<plugin-id>-v<semver>` | `hyper-v-control-v0.1.0` |
| Release title | `<Display name> <version> — <platform or package role>` | `Hyper-V Control 0.1.0 — Windows x64 MCP and Plugin` |
| Standalone executable | `<plugin-id>-<rid>-v<version>.exe` | `hyper-v-control-win-x64-v0.1.0.exe` |
| Complete plugin ZIP | `<plugin-id>-plugin-v<version>[-<rid>].zip` | `hyper-v-control-plugin-v0.1.0-win-x64.zip` |
| Multi-file runtime ZIP, when needed | `<plugin-id>-runtime-<rid>-v<version>.zip` | Use when an EXE requires adjacent runtime files |
| Checksums | `<plugin-id>-v<version>-SHA256SUMS.txt` | `hyper-v-control-v0.1.0-SHA256SUMS.txt` |
| Asset inventory | `<plugin-id>-v<version>-release.json` | `hyper-v-control-v0.1.0-release.json` |

Use .NET-style runtime identifiers such as `win-x64`, `win-arm64`, `linux-x64`, and `osx-arm64`. A filename should stay unambiguous when copied into a shared Downloads directory. An installed executable may have a stable local name such as `HyperVControl.exe`; the installer maps the namespaced download to that name inside the plugin's own versioned directory.

Skills belong inside the complete plugin ZIP and are installed with the plugin. Do not publish a redundant standalone `.skill` download for a new plugin release. Historical standalone skill assets remain available at their original release URLs.

For a genuinely independent runtime version, use a separate `<runtime-id>-runtime-v<version>` stream and pin the relationship from the consuming plugin. A plugin may update documentation without rebuilding a large, unchanged runtime. Existing Rizin runtime tags (`rizin-v0.3.1`) and asset names are legacy compatibility contracts and are not renamed.

## Download and upgrade isolation

- Never use repository-wide `/releases/latest` or `/releases/latest/download/...` from an installer. The newest release may belong to a different plugin.
- Prefer an exact release tag, platform, asset filename and SHA-256 pin in the consuming plugin. Hyper-V's `runtime.json` contains all four; its installer rejects a mismatched plugin, tag, platform or asset URL before downloading.
- When latest-in-stream discovery is necessary, filter both tag family and asset name, skip drafts/prereleases, paginate until the stream is found, and verify the selected asset checksum. Rizin's installer follows this path and retains its legacy `SHA256SUMS` lookup.
- Keep install directories separate and versioned. Reuse a matching verified installation; do not overwrite a different checksum in place or alter a global PATH implicitly. Rizin's user PATH change remains opt-in; Hyper-V uses an absolute executable path.
- Keep published runtime binaries and plugin ZIPs immutable. Fix their contents with a new patch version. Release notes and inventories may be corrected, and redundant ancillary downloads may be removed with their checksum entries; these editorial changes must preserve the executable/ZIP hashes and existing runtime URLs.
- Publish independent plugin releases with `--latest=false`. The global GitHub Latest badge is not a plugin index. This preserves existing legacy behavior; the table above is the entrypoint for choosing a component.

## Packaging and publication

1. Build/test the component and its installer in an isolated directory. For Hyper-V, use .NET SDK `10.0.401` and `scripts/build-hyperv-control.ps1`. The runtime patch is pinned to `10.0.12`; no compiler or .NET installation is needed by end users.
2. Commit the runtime pin together with the implementation and packaging. CI verifies that rebuilding the EXE does not change the committed pin. Source paths and Git revision metadata are excluded from the binary identity; GitHub's release tag records the source commit. This avoids a circular dependency between a commit ID and the binary checksum committed within it.
3. Open and validate the PR, merge it, and tag the exact merged commit. Include only the component's assets in its release. Do not upload all files in a shared `build` directory.
4. Prepare a draft release with explicit tag/title/target, upload the named EXE/ZIP, asset inventory and checksum file, then publish it without moving the repository-wide Latest badge.
5. Download the published assets, verify their hashes against the committed pin and inventory, and exercise the real installer in a temporary directory before reporting completion.

For Hyper-V, upload these four files from `build/hyperv-control/release`:

```text
hyper-v-control-win-x64-v0.1.0.exe
hyper-v-control-plugin-v0.1.0-win-x64.zip
hyper-v-control-v0.1.0-release.json
hyper-v-control-v0.1.0-SHA256SUMS.txt
```

The ZIP contains the EXE, manifests, skill, documentation, launcher, installer, and licenses. The EXE-only download is for direct stdio MCP configuration. Local convenience aliases (`HyperVControl.exe`, `SHA256SUMS`) are not additional public Hyper-V assets. The inventory records primary asset names, roles, platforms, sizes and hashes; the checksum file also covers the inventory and does not hash itself.

Rizin RE Toolkit 0.4.1 is an installer-only maintenance release; its runtime remains 0.3.1. Its download page provides the complete plugin ZIP, namespaced asset inventory and namespaced checksum file. The existing `rizin-v0.3.1` runtime release is left untouched. Future Rizin runtime releases retain the existing runtime asset family and legacy `SHA256SUMS` while older runtime installers are supported; plugin-only releases do not need that compatibility alias.

## Validation

`scripts/test-release-isolation.ps1` substitutes only HTTP responses while performing real hash checks, extraction, version execution and installation. It tests a full page of unrelated releases before Rizin, a misleading tag with a Rizin-looking asset, drafts, prereleases, corrupt downloads, idempotent Hyper-V installation and coexistence of installation directories. The small synthetic Rizin version probe validates installer behavior; the actual published Rizin runtime is separately downloaded and executed for release verification.
