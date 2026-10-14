# Building the Windows x64 release

The runtime starts with official Rizin 0.9.1 shared binaries. Plugins are rebuilt from locked upstream commits plus Windows patches. The build inputs live under `runtimes/rizin/`: `bundle.lock.json` (component commits, dependency versions, download hashes), `patches/`, the build, packaging, and verification scripts, the smoke fixture, and the UTF-8 application manifest. The assembled runtime also embeds `docs/guides/rizin-runtime.md` and `docs/reference/rizin-third-party.md` as its README and third-party notice. Prerequisites: VS 2026 C++ x64 tools, Windows SDK, CMake, Git, and Python 3.12+.

```powershell
git clone https://github.com/JerryLinLinLin/jerry-plugin-marketplace.git
cd jerry-plugin-marketplace
py -3 -m venv build\tools
& .\build\tools\Scripts\python.exe -m pip install -r runtimes\rizin\build-requirements.txt
powershell -NoProfile -ExecutionPolicy Bypass -File runtimes\rizin\build-bundle.ps1
```

`enter-build-env.ps1` locates the newest x64 toolchain with vswhere and changes only the current process environment. Microsoft's linker stays ahead of Git's unrelated link.exe.

The build fetches each upstream component into `build/sources/<name>` at the commit locked in `bundle.lock.json` (a shallow, detached checkout; the repository itself contains no submodules, because Claude Code would clone them into every user's plugin cache), applies the patches once, hash-checks downloads, builds plugins, and assembles `build/portable/rizin`. `build_bundle.py --sources-only` fetches and patches the sources without building. Compiler headers/libraries stay in `build/rizin`. Logs are in `build/logs`. Assembly refuses to merge into an existing `build/portable/rizin`; move that generated output aside before reassembly.

YARA is checked out sparsely before materializing files, excluding its antivirus-sensitive fuzz corpus. No antivirus exclusion or protection change is needed. Avoid recursively checking out that separate Meson dependency's test corpus.

RetDec's upstream SDK revision is unchanged. The hash-locked `rizin-retdec-sdk-win-x64-v0.3.1.zip` asset contains the headers, libraries, CMake files, support data, and licenses needed to rebuild core_retdec.dll for Rizin 0.9.1. These files were preserved byte-for-byte from the former v0.1.0 bundle; `sdk-provenance.json` records their origin. The SDK is downloaded from the retained `rizin-v0.3.1` release into `build/retdec-sdk`, so a clean build does not depend on deleted releases. VS 2026 is needed to link that SDK. OpenSSL 1.1.1w remains its ABI dependency; YARA uses OpenSSL 3.5.9 LTS.

## Verification

Compile the benign fixture and dump only its own process:

```powershell
. .\runtimes\rizin\enter-build-env.ps1
New-Item -ItemType Directory -Force build\fixtures | Out-Null
cl /nologo /Od /MD /Zi runtimes\rizin\fixtures\smoke.c /Febuild\fixtures\smoke.exe /Fobuild\fixtures\smoke.obj /Fdbuild\fixtures\smoke.pdb /link dbghelp.lib /INCREMENTAL:NO
& .\build\fixtures\smoke.exe "$PWD\build\fixtures\smoke.dmp"
& .\build\tools\Scripts\python.exe runtimes\rizin\verify-bundle.py --runtime build\portable\rizin --fixture build\fixtures\smoke.exe --dump build\fixtures\smoke.dmp --output build\verification
```

The fixture uses CREATE_NEW: select a fresh dump path when rerunning it. Verification checks PE architectures/imports, all three decompilers and JSON output, SLEIGH, YARA PE/hash rules, FLIRT listing/generation/application, MDMP code recovery, and raw-memory addressing. It runs with a minimal PATH and no SLEIGH/RetDec configuration, preserving stdout/stderr and a JSON report.

After packaging, extract the archive into a new path with spaces and Unicode and rerun verification. After packaging, run `scripts/test-rizin-installer.ps1 -ReleaseDir build\release`: it exercises the plugin's installer against the packaged archive and the real executable without network access (without `-ReleaseDir` it uses a synthetic probe). Interrupted or stale source checkouts under `build/sources` are refetched automatically when their commit differs from the lock file.

## Packaging

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File runtimes\rizin\package-bundle.ps1
```

This creates only the runtime ZIP, runtime manifest, and SHA256SUMS, plus a verification report when one has been produced separately. Use a clean output directory. The archive excludes marketplace manifests and skills.

The runtime version comes from `bundle.lock.json`. For a new runtime release, increment it and tag the tested source commit with `rizin-v<bundle-version>`. Published runtime archives are immutable. After the assets are published, record the new tag, archive name, and SHA-256 in `plugins/rizin-re-toolkit/runtime.json`: the plugin installs exactly that pinned archive and never resolves a newer release on its own.

Plugin and skill changes ship through the marketplace source under `plugins/rizin-re-toolkit/`; they do not create a separate plugin ZIP or GitHub Release. Earlier validation reports retain their original IDs and versions as historical evidence.

Validate discovery without changing user configuration:

```powershell
codex -c 'marketplaces.jerry-plugin-marketplace={source="C:/path/to/repository",source_type="local"}' plugin list --available --json --marketplace jerry-plugin-marketplace
```

The portable manifest follows the Agent Plugins schema; the Codex compatibility manifest retains matching identity, version, and interface metadata. Validate the skill with Codex's skill-creator validator when available.

The assembly step embeds `runtimes/rizin/windows-utf8.manifest` into every CLI executable with the SDK manifest tool. This activates UTF-8 per process and requires Windows 10 version 1903 or later for Unicode paths. It does not change the Windows system locale. See [Microsoft documentation](https://learn.microsoft.com/en-us/windows/apps/design/globalizing/use-utf8-code-page).
