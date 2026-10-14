# Maintaining and extending the marketplace

Each plugin is an independent product installed through the marketplace. Keep its installed files self-contained and its runtime dependencies explicit. GitHub Releases carry native runtimes; do not publish separate plugin ZIPs or skill downloads. Shared code belongs at the repository level only when multiple plugins actually use it.

## Repository map

| Location | Responsibility |
| --- | --- |
| `.agents/plugins/marketplace.json` | Canonical discovery catalog and Codex entrypoint: plugin IDs, locations, and categories |
| `.claude-plugin/marketplace.json` | Generated Claude Code discovery catalog |
| `plugins/<id>/` | Installable package: manifest, skill, user documentation, helpers, and licenses |
| `src/<Product>/` | Native application or MCP implementation; currently Hyper-V Control |
| `tests/` | Native test projects (`<Product>.Tests/`) and per-plugin maintainer experiment harnesses (`<plugin-id>/`); never installed |
| `runtimes/rizin/` | Rizin runtime build inputs: lock file, upstream patches, build/package/verify scripts, smoke fixture, and UTF-8 manifest |
| `scripts/` | Repository-wide checks, the Hyper-V builder, and the installer/launcher tests the Windows CI job runs; never an installed plugin dependency |
| `docs/` | [Documentation index](docs/README.md): guides, references, validation records, and byte-for-byte archives of imported material |
| `build/` | Ignored generated files: fetched upstream sources, downloads, binaries, and local experiment output |

Component-specific tooling lives in its own directory (`runtimes/rizin/`, `tests/<plugin-id>/`) or carries the component's name (`build-hyperv-control.ps1`, `test-rizin-installer.ps1`, `test-hyperv-launcher.ps1`). `scripts/` holds only repository-wide checks and the scripts CI runs. Do not add unrelated behavior to the Rizin builders.

Do not add Git submodules: Claude Code clones a marketplace with every submodule into each user's plugin cache. Record build-time sources in a lock file and fetch them into ignored `build/` at build time, as `runtimes/rizin/build_bundle.py` does. A checkout that initialized the old submodules keeps an untracked `sources/` directory; delete it rather than adding it back (the checker rejects gitlink entries).

Within Hyper-V Control, `HyperVTools*` exposes the MCP contract, `Services/` owns host/guest operations and resource lifetimes, and `Extracted/` contains the core Hyper-V service, console and guest UI implementation. Keep protocol handling out of the extracted layer. Changes to VM state, credentials, elevation, or debugger lifetime need behavior tests at their corresponding boundary.

## Sources of truth

- Edit `plugins/<id>/plugin.json` for plugin identity, version, and interface metadata. Both clients cache an installed plugin by its version string, so bump the version whenever installed content changes. Run `python scripts/check-marketplace.py --sync` to generate `.codex-plugin/plugin.json`, `.claude-plugin/plugin.json`, and the root `.claude-plugin/marketplace.json`; do not maintain these compatibility copies independently. Claude marketplace entries come from the canonical catalog and plugin descriptions; marketplace owner/description metadata lives in the checker's `claude_marketplace` function.
- Edit a plugin's portable `mcp.json` for MCP commands, using `${PLUGIN_ROOT}` for paths inside the package. The same sync command generates `.codex-plugin/mcp.json` with `${PLUGIN_ROOT}` and `.mcp.json` with `${CLAUDE_PLUGIN_ROOT}`. Point the canonical `com.openai.mcpServers` field at `./.codex-plugin/mcp.json`. Claude automatically reads the root `.mcp.json`; keeping the client files separate prevents unresolved path variables. Both clients share the same `skills/` and runtime helpers.
- Native downloads use the plugin's `runtime.json` for the exact release tag, asset, and SHA-256; both installers verify the download against it and never resolve a latest release. Rizin's build inputs are in `runtimes/rizin/bundle.lock.json`; after publishing a new Rizin runtime, update the plugin's `runtime.json` pin. Keep dependency versions in metadata and lockfiles.
- Hyper-V's project, CLI, and protocol versions must match its runtime pin. The plugin version can advance independently for launcher or skill updates. The repository checker rejects inconsistent runtime updates; the build verifies the executable version and hash.
- README files explain purpose, setup, and usage. Keep current version numbers, release dates, and changelogs out of them. The [download catalog](docs/guides/releases.md#release-index) is the maintained entrypoint for current releases.
- Skills explain how an agent should choose tools and interpret results. Put detailed task procedures in their `references/` directory and scripts needed at runtime inside the plugin.
- Record experiments in validation reports and keep their harnesses under `tests/<plugin-id>/`. Imported material lives under `docs/archive/<component>/` with an `import-manifest.json`; the checker verifies those files byte-for-byte, so never edit them. Plugins ship only maintained content.

Links inside distributed plugin documentation must resolve inside that plugin or use an absolute repository/web URL. A relative link to `../../docs/` works in a checkout but breaks in the installed plugin cache.

## Add a plugin

1. Choose a stable lowercase ID and create `plugins/<id>/` with `plugin.json`, `README.md`, `LICENSE`, and `skills/<skill-id>/SKILL.md`. Keep plugin content in English. Add only the references, assets, and helpers that the installed plugin needs.
2. Add a local catalog entry pointing to `./plugins/<id>`, then run the manifest sync/check command. New catalog plugins are discovered by the checker automatically.
3. If it needs a native executable, put its source and tests under `src/` and `tests/`. Give it a dedicated builder, verified installer, and versioned installation directory. A source-only skill/CLI plugin does not need an MCP server or binary release.
4. Add component-specific validation to the existing PR workflow. Keep expensive job selection in `scripts/ci-plan.py`; extend it for the component's actual inputs. Do not introduce a second push or tag workflow for the same checks.
5. Test the marketplace-installed plugin from a fresh directory. It must work without the repository checkout, another plugin, or a developer's absolute paths. Publish runtime assets only when the plugin requires them; follow the [release guide](docs/guides/releases.md).

## Check a change

```powershell
python scripts/check-marketplace.py
python scripts/test-runtime-packaging.py
```

These fast checks validate both catalogs, Codex/Claude manifests and MCP configurations, bundled skill paths, runtime pins, plugin content boundaries, archive integrity, documentation layout and every Markdown link (including skill references), README policy, and runtime packaging. They do not install plugin dependencies or operate a VM.

When Claude Code is available, also run its native validator against the catalog and each plugin:

```powershell
claude plugin validate . --strict
Get-ChildItem plugins -Directory | ForEach-Object {
    claude plugin validate $_.FullName --strict
    if ($LASTEXITCODE -ne 0) { throw "Claude validation failed: $($_.Name)" }
}
```

To try a local skill without registering the marketplace, start `claude --plugin-dir ./plugins/rizin-re-toolkit` and invoke `/rizin-re-toolkit:rizin-re-toolkit`. Alternatively, `claude plugin marketplace add .` registers the checkout, after which the install commands in the [README](README.md#claude-code) use its local packages. See Claude's [manifest reference](https://code.claude.com/docs/en/plugins-reference) and [marketplace reference](https://code.claude.com/docs/en/plugins/marketplace-reference) for the supported fields and paths.

For MCP configuration changes, run `./scripts/test-hyperv-launcher.ps1` on Windows. It exercises both clients' commands from an isolated plugin path containing spaces with a controlled executable, without operating a VM. For installer or runtime-pin changes, run `./scripts/test-rizin-installer.ps1` (synthetic archive, no network) and `./scripts/test-release-isolation.ps1` (needs a built Hyper-V executable).

The single PR workflow runs these checks for all changes. It runs Windows build/protocol/installer checks only when their inputs change. Pure documentation changes avoid native builds, newer commits cancel obsolete runs, and merging or tagging does not repeat CI. Use a manual workflow run when downloadable build artifacts are needed.

For behavior changes, also run the affected component's documented tests: [Hyper-V Control](plugins/hyper-v-control/README.md#development-and-validation), [Rizin](docs/guides/rizin-build.md), [Frida](tests/frida-use/README.md), or [Capstone](plugins/capstone-binary-patching/README.md#validation-and-boundaries). Live tests must have a defined target, restoration plan, and recorded limits; CI never discovers and operates on a developer's VMs automatically.

Before removing a release, check both runtime installers and build lockfiles for references. Preserve active dependencies first. Published primary EXEs and ZIPs are immutable; publish changed bytes under a new version. Documentation-only edits do not require rebuilding an unchanged runtime.
