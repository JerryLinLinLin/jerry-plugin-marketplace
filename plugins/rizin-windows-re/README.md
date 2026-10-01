# Rizin Windows RE

A portable Windows x64 CLI and Codex skill for native binary and memory-dump analysis.

Download the runtime ZIP and SHA256SUMS from [release rizin-v0.3.1](https://github.com/JerryLinLinLin/rizin-win64-bundle/releases/tag/rizin-v0.3.1), verify the archive hash, and extract the whole rizin folder. Run `rizin\bin\rizin.exe -v` by absolute path, or add its bin directory to the current process PATH. Non-system runtime DLLs are included. Windows 10 (1903+) or Windows 11 x64 is required; administrator installation is unnecessary.

| Component | Version / upstream revision | Command |
| --- | --- | --- |
| Rizin | 0.9.1 | rizin and companion tools |
| rz-ghidra | Current upstream 64ea54a, adapted for Rizin 0.9.1 | pdg, pdgj |
| jsdec | Current upstream 9fc6098 | pdd, pddj |
| rz-retdec | Current upstream 4ac6b29, RetDec 5.0 | pdz, pdzj |
| rz-libyara | Current upstream 451e349, YARA 4.5.8 | yaral, yaraMj |
| sigdb | Current upstream 4addbed | Fl, Fa, Fs |

Versions were checked on October 1, 2026. Exact revisions, dependency hashes, and patch hashes are in the runtime's bundle-manifest.json and the repository's bundle.lock.json.

## Agent setup

Install **Rizin Windows RE** from **My Plugin Marketplace** in Codex. The [skill](skills/rizin-windows-re/SKILL.md) teaches the agent to download the CLI, run noninteractive analysis, compare decompilers, and investigate memory dumps.

The [PowerShell installer](skills/rizin-windows-re/scripts/install.ps1) supports a user-level install, an explicit destination, a fixed release tag, and optional user PATH registration. It verifies SHA-256 before running the extracted CLI. The large runtime is a separate release asset.

## Examples

```powershell
rizin -q -N -e scr.color=0 -c iI sample.exe
rizin -A -q -N -e scr.color=0 -c 'pdz @ main' sample.exe
rizin -q -N -e scr.color=0 -c iI -c omlj -c il sample.dmp
```

Use a real function/address instead of assuming a main symbol exists. For dumps, inspect captured mappings and analyze a bounded function before decompilation. The [dump workflow](skills/rizin-windows-re/references/memory-dumps.md) covers MDMP, DMP64, ELF core, raw memory, ASLR, and address translation.

## Portability and changes

- Ghidra SLEIGH and RetDec support data resolve relative to the executable's installation. Move the entire folder together.
- Each CLI activates UTF-8 through its application manifest, supporting Unicode paths without changing system locale settings.
- Windows linking and Rizin 0.9.1 adapters are maintained as source patches.
- The current Ghidra upstream PE import-slot fix is included.
- jsdec reads the supported JSON color-palette command.
- RetDec leaves unknown return types unspecified instead of forcing void and discarding calculations.
- VC++ runtime DLLs ship beside the executables. YARA uses OpenSSL 3.5.9; the unchanged upstream RetDec SDK requires legacy OpenSSL 1.1.1w, included separately.

The runtime includes licenses/ and THIRD-PARTY.md. The plugin/skill is MIT; redistributed components retain their own licenses.
