# Maintaining and extending the marketplace

Each plugin is an independent product installed through the marketplace. Keep its installed files self-contained and its runtime dependencies explicit. GitHub Releases carry native runtimes; do not publish separate plugin ZIPs or skill downloads. Shared code belongs at the repository level only when multiple plugins actually use it.

## Repository map

| Location | Responsibility |
| --- | --- |
| `.agents/plugins/marketplace.json` | Discovery catalog: plugin IDs, locations, and categories |
| `plugins/<id>/` | Installable package: manifest, skill, user documentation, helpers, and licenses |
| `src/<Product>/` | Native application or MCP implementation; currently Hyper-V Control |
| `tests/<Product>.Tests/` | Native protocol and behavior validation |
| `scripts/` | Maintainer builds, packaging, and repository checks; never an installed plugin dependency |
| `docs/` | [Documentation index](docs/README.md): guides, references, and validation records grouped by component and run |
| `sources/`, `patches/`, `bundle.lock.json` | Pinned Rizin upstream sources, compatibility patches, and build dependencies |
| `build/` | Ignored generated files, downloads, binaries, and local experiment output |

The older `build-bundle`, `package-bundle`, and `verify-bundle` scripts belong to the Rizin runtime. Hyper-V uses `build-hyperv-control.ps1`. New component-specific tooling should use explicit names or its own subdirectory; do not add unrelated behavior to the Rizin builders.

Within Hyper-V Control, `HyperVTools*` exposes the MCP contract, `Services/` owns host/guest operations and resource lifetimes, and `Extracted/` contains adapted RaivenX functionality. Keep protocol handling out of the extracted layer. Changes to VM state, credentials, elevation, or debugger lifetime need behavior tests at their corresponding boundary.

## Sources of truth

- Edit `plugins/<id>/plugin.json` for plugin identity, version, and interface metadata. Run `python scripts/check-marketplace.py --sync` to generate `.codex-plugin/plugin.json`; do not maintain the compatibility copy independently.
- Compiled downloads use the plugin's `runtime.json` for the exact artifact and hash. Rizin's independently versioned runtime and build inputs are in `bundle.lock.json`. Keep dependency versions in metadata and lockfiles.
- Hyper-V's project, CLI, and protocol versions must match its runtime pin. The plugin version can advance independently for launcher or skill updates. The repository checker rejects inconsistent runtime updates; the build verifies the executable version and hash.
- README files explain purpose, setup, and usage. Keep current version numbers, release dates, and changelogs out of them. The [download catalog](docs/guides/releases.md#release-index) is the maintained entrypoint for current releases.
- Skills explain how an agent should choose tools and interpret results. Put detailed task procedures in their `references/` directory and scripts needed at runtime inside the plugin.
- Record experiments in validation reports. Imported Frida material under `references/legacy/` and `docs/migration/` is historical evidence, not the maintained entrypoint; preserve its provenance and licenses.

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

These fast checks validate the catalog, compatibility manifests, bundled skill paths, runtime pins, documentation layout/links, README policy, and runtime packaging. They do not install plugin dependencies or operate a VM.

The single PR workflow runs these checks for all changes. It runs Windows build/protocol/installer checks only when their inputs change. Pure documentation changes avoid native builds, newer commits cancel obsolete runs, and merging or tagging does not repeat CI. Use a manual workflow run when downloadable build artifacts are needed.

For behavior changes, also run the affected component's documented tests: [Hyper-V Control](plugins/hyper-v-control/README.md#development-and-validation), [Rizin](docs/guides/rizin-build.md), [Frida](scripts/frida-use-experiments/README.md), or [Capstone](plugins/capstone-binary-patching/README.md#validation-and-boundaries). Live tests must have a defined target, restoration plan, and recorded limits; CI never discovers and operates on a developer's VMs automatically.

Before removing a release, check both runtime installers and build lockfiles for references. Preserve active dependencies first. Published primary EXEs and ZIPs are immutable; publish changed bytes under a new version. Documentation-only edits do not require rebuilding an unchanged runtime.
