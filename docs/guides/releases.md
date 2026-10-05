# Runtime releases

Install plugins through **Jerry's Plugin Marketplace** in Codex or Claude Code using the [installation guide](../../README.md#install). The marketplace distributes their manifests, skills, launchers, and setup helpers. GitHub Releases distribute native runtimes and their supporting notices, checksums, and build metadata. Do not publish plugin ZIPs or standalone skill files.

This repository supports the [Codex marketplace workflow](https://developers.openai.com/plugins/build/plugins#add-a-marketplace-from-the-cli) and [Claude Code marketplace workflow](https://code.claude.com/docs/en/plugin-marketplaces).

## Release index

| Runtime | Release | Download |
| --- | --- | --- |
| Hyper-V Control | [Windows x64](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/tag/hyper-v-control-v0.1.0) | Self-contained MCP executable |
| Rizin | [Windows x64](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases/tag/rizin-v0.3.1) | Portable runtime; RetDec build SDK is a separate maintainer asset |

Frida and Capstone dependencies are installed by their plugin setup workflows. They do not need a binary release in this repository.

## Versions and installation

Plugin versions live in `plugins/<id>/plugin.json`. Run `python scripts/check-marketplace.py --sync` after changing them to update both clients' generated manifests. Native runtime versions are independent: Hyper-V records its executable pin in `runtime.json`, and Rizin records its runtime/build inputs in `bundle.lock.json`. A launcher or skill update can ship through the marketplace without rebuilding an unchanged executable.

Hyper-V's launcher downloads and verifies a missing runtime before starting the MCP. Rizin's skill invokes its verified runtime installer when needed. Each component uses its own versioned installation directory. Never use the repository-wide Latest release to resolve a runtime: pin an exact asset, or filter and paginate within the intended runtime stream.

## Asset naming

| Item | Pattern |
| --- | --- |
| Runtime tag | `<component>-v<version>` |
| Executable | `<component>-<rid>-v<version>.exe` |
| Runtime archive | `<component>-runtime-<rid>-v<version>.zip` |
| Notices | `<component>-v<version>-NOTICES.txt` |
| Asset inventory | `<component>-v<version>-release.json` |
| Checksums | `<component>-v<version>-SHA256SUMS.txt` |

Use platform identifiers such as `win-x64`. Keep established Rizin runtime filenames and `SHA256SUMS` compatible with its installer. Runtime archives contain executable dependencies and support data; marketplace content stays under `plugins/`.

## Build and publish

1. Build and test the runtime. Hyper-V uses `scripts/build-hyperv-control.ps1`; its SDK and framework are pinned in the CI workflow and project. Rizin uses the [build guide](rizin-build.md).
2. Commit matching runtime pins and metadata. Run the repository checks and affected component tests, then merge the PR.
3. Tag the merged commit. Publish only the builder's runtime assets, notices, inventory, and checksums. Inspect explicit filenames; do not upload an entire shared build directory.
4. Download the published files, verify their hashes, and exercise the installer in a fresh directory.
5. Update the release index above. Keep release notes to a short introduction, user-facing changes, and download/setup links.

Hyper-V outputs four files in `build/hyperv-control/runtime-release`: the EXE, notices, inventory, and checksum file. Rizin outputs its runtime ZIP, manifest, checksums, and any separately produced verification report. Packaging rejects unrelated files in the output directory.

Publish changed runtime bytes under a new version. Preserve current runtime URLs and hashes during metadata corrections. Before retiring an old release, check both installers and build lockfiles and migrate any active dependency. Keep source tags and historical validation records.

## CI

One workflow runs for PRs targeting `main`. Fast marketplace/runtime-packaging checks run for every PR; `scripts/ci-plan.py` selects expensive Windows validation when its inputs change. Updated PRs cancel obsolete runs. Pushes, merge commits, and tags do not repeat validation.

Manual workflow runs can upload runtime artifacts. They do not publish a GitHub Release automatically. See [CONTRIBUTING](../../CONTRIBUTING.md) for component boundaries and local checks.
