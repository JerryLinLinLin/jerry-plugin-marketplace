# Runtime releases

Install plugins through **Jerry's Plugin Marketplace** in Codex or Claude Code using the [installation guide](../../README.md#install). The marketplace distributes their manifests, skills, launchers, and setup helpers. GitHub Releases distribute native runtimes and their supporting notices, checksums, and build metadata. Do not publish plugin ZIPs or standalone skill files.

This repository supports the [Codex marketplace workflow](https://developers.openai.com/plugins/build/plugins#add-a-marketplace-from-the-cli) and [Claude Code marketplace workflow](https://code.claude.com/docs/en/plugin-marketplaces).

## Release index

| Runtime | Release | Download |
| --- | --- | --- |
| Hyper-V Control | [Windows x64](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/tag/hyper-v-control-v0.2.1) | Self-contained MCP executable |
| Rizin | [Windows x64](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/tag/rizin-v0.3.1) | Portable runtime; RetDec build SDK is a separate maintainer asset |

Frida and Capstone dependencies are installed by their plugin setup workflows. They do not need a binary release in this repository.

## Versions and installation

Plugin versions live in `plugins/<id>/plugin.json`. Both clients cache an installed plugin by that version string, so bump it whenever installed content changes (skills, references, scripts, or `runtime.json`), then run `python scripts/check-marketplace.py --sync` to update both clients' generated manifests. Native runtime versions are independent of plugin versions: each plugin pins the exact release it installs (tag, asset, SHA-256) in its `runtime.json`, and Rizin's build inputs live in `runtimes/rizin/bundle.lock.json`. A launcher or skill update can ship through the marketplace without rebuilding an unchanged executable.

Hyper-V's launcher downloads and verifies a missing runtime before starting the MCP. Rizin's skill invokes its runtime installer when no usable installation exists. Each component uses its own versioned installation directory. Both installers download only the pinned asset from its release download URL and verify its SHA-256; neither queries the GitHub API or resolves the repository-wide Latest release, so a newer runtime always ships as a plugin update that changes `runtime.json`.

## Asset naming

| Item | Pattern |
| --- | --- |
| Runtime tag | `<component>-v<version>` |
| Executable | `<component>-<rid>-v<version>.exe` |
| Runtime archive | `<component>-runtime-<rid>-v<version>.zip` |
| Notices | `<component>-v<version>-NOTICES.txt` |
| Asset inventory | `<component>-v<version>-release.json` |
| Checksums | `<component>-v<version>-SHA256SUMS.txt` |

Use platform identifiers such as `win-x64`. Keep publishing the Rizin runtime archive with its `SHA256SUMS` for manual verification; the installer uses the archive name and hash pinned in `runtime.json`. Runtime archives contain executable dependencies and support data; marketplace content stays under `plugins/`.

## Build and publish

1. Build and test the runtime. Hyper-V uses `scripts/build-hyperv-control.ps1`; its SDK and framework are pinned in the CI workflow and project, and CI later verifies that a rebuild matches the published executable apart from compiler-recorded build identifiers. Rizin uses the [build guide](rizin-build.md).
2. Commit matching metadata. Hyper-V's builder writes its `runtime.json` pin from the built executable; Rizin's lock file carries the new `bundleVersion`. Run the repository checks and affected component tests, then merge the PR.
3. Tag the merged commit. Publish only the builder's runtime assets, notices, inventory, and checksums. Inspect explicit filenames; do not upload an entire shared build directory.
4. Download the published files, verify their hashes, and exercise the installer in a fresh directory. For Rizin, run `scripts/test-rizin-installer.ps1 -ReleaseDir build\release` against the packaged output before publishing; after updating the pin, `install.ps1 -Destination <fresh directory>` exercises the published release.
5. For Rizin, record the published tag, archive name, and SHA-256 in `plugins/rizin-re-toolkit/runtime.json`, bump the plugin version in `plugins/rizin-re-toolkit/plugin.json`, run the sync command, and merge; installs follow the new pin from then on.
6. Update the release index above. Keep release notes to a short introduction, user-facing changes, and download/setup links.

Hyper-V outputs four files in `build/hyperv-control/runtime-release`: the EXE, notices, inventory, and checksum file. Rizin outputs its runtime ZIP, manifest, checksums, and any separately produced verification report. Packaging rejects unrelated files in the output directory.

Publish changed runtime bytes under a new version. Preserve current runtime URLs and hashes during metadata corrections. Before retiring an old release, check both installers and build lockfiles and migrate any active dependency. Keep source tags and historical validation records.

## CI

One workflow runs for PRs targeting `main`. Fast marketplace/runtime-packaging checks run for every PR; `scripts/ci-plan.py` selects expensive Windows validation when its inputs change. Updated PRs cancel obsolete runs. Pushes, merge commits, and tags do not repeat validation.

Manual workflow runs can upload runtime artifacts. They do not publish a GitHub Release automatically. See [CONTRIBUTING](../../CONTRIBUTING.md) for component boundaries and local checks.
