# Building the Windows x64 release

The runtime starts with official Rizin 0.9.1 shared binaries. Plugins are rebuilt from locked upstream commits plus Windows patches. Prerequisites: VS 2026 C++ x64 tools, Windows SDK, CMake, Git, and Python 3.12+.

```powershell
git clone https://github.com/JerryLinLinLin/jerry-plugin-marketplace.git
cd jerry-plugin-marketplace
git submodule update --init --depth 1
py -3 -m venv build\tools
& .\build\tools\Scripts\python.exe -m pip install -r scripts\build-requirements.txt
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-bundle.ps1
```

enter-build-env.ps1 locates the newest x64 toolchain with vswhere and changes only the current process environment. Microsoft's linker stays ahead of Git's unrelated link.exe.

The build verifies source revisions, applies patches once, hash-checks downloads, builds plugins, and assembles build/portable/rizin. Compiler headers/libraries stay in build/rizin. Logs are in build/logs. Assembly refuses to merge into an existing build/portable/rizin; move that generated output aside before reassembly.

YARA is checked out sparsely before materializing files, excluding its antivirus-sensitive fuzz corpus. No antivirus exclusion or protection change is needed. Avoid recursively checking out that separate Meson dependency's test corpus.

RetDec's upstream SDK revision is unchanged. The hash-locked SDK from release v0.1.0 is reused while core_retdec.dll is rebuilt for Rizin 0.9.1. VS 2026 is needed to link that SDK. OpenSSL 1.1.1w remains its ABI dependency; YARA uses OpenSSL 3.5.9 LTS.

## Verification

Compile the benign fixture and dump only its own process:

```powershell
. .\scripts\enter-build-env.ps1
New-Item -ItemType Directory -Force build\fixtures | Out-Null
cl /nologo /Od /MD /Zi scripts\fixtures\smoke.c /Febuild\fixtures\smoke.exe /Fobuild\fixtures\smoke.obj /Fdbuild\fixtures\smoke.pdb /link dbghelp.lib /INCREMENTAL:NO
& .\build\fixtures\smoke.exe "$PWD\build\fixtures\smoke.dmp"
& .\build\tools\Scripts\python.exe scripts\verify-bundle.py --runtime build\portable\rizin --fixture build\fixtures\smoke.exe --dump build\fixtures\smoke.dmp --output build\verification
```

The fixture uses CREATE_NEW: select a fresh dump path when rerunning it. Verification checks PE architectures/imports, all three decompilers and JSON output, SLEIGH, YARA PE/hash rules, FLIRT listing/generation/application, MDMP code recovery, and raw-memory addressing. It runs with a minimal PATH and no SLEIGH/RetDec configuration, preserving stdout/stderr and a JSON report.

After packaging, extract the archive into a new path with spaces and Unicode and rerun verification. Test the release installer with a separate user-writable destination too.

## Packaging

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-bundle.ps1 -Version 0.3.1
```

This creates a runtime ZIP, plugin ZIP, standalone skill package, and SHA256SUMS. The command reproduces the currently locked runtime version. For a new CLI release, increment the bundle version in the lock file and tag the tested source commit with a new `rizin-v<bundle-version>` tag. Other marketplace plugins can use their own tag prefixes.

Plugin/skill releases are now independent of the CLI bundle version. The runtime ZIP uses `bundle.lock.json` and the requested runtime version; plugin/skill filenames use the version in `plugins/rizin-re-toolkit/plugin.json`.

For a skill-only or metadata update, package without rebuilding or republishing the runtime:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-bundle.ps1 -PluginOnly -Version 0.4.0 -OutputDir build\plugin-release-0.4.0
```

Publish those files under `rizin-re-toolkit-v0.4.0`. The CLI installer searches release assets for the runtime prefix, so a newer plugin-only release is not mistaken for a runtime download. The native runtime remains at `rizin-v0.3.1` until a separate CLI release is built and tested. Earlier validation reports retain their original plugin IDs and versions as historical evidence.

Validate discovery without changing user configuration:

```powershell
codex -c 'marketplaces.my-plugin-marketplace={source="C:/path/to/repository",source_type="local"}' plugin list --available --json --marketplace my-plugin-marketplace
```

The portable manifest follows the Agent Plugins schema; the Codex compatibility manifest retains matching identity, version, and interface metadata. Validate the skill with Codex's skill-creator validator when available.

The assembly step embeds scripts/windows-utf8.manifest into every CLI executable with the SDK manifest tool. This activates UTF-8 per process and requires Windows 10 version 1903 or later for Unicode paths. It does not change the Windows system locale. See [Microsoft documentation](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/use-utf8-code-page).
