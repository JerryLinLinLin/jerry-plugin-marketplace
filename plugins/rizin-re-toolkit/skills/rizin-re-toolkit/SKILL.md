---
name: rizin-re-toolkit
description: >-
  Native binary reverse engineering, disassembly, decompilation, malware triage, and memory-dump
  analysis through Rizin RE Toolkit. Use as the default RE workflow, including requests mentioning
  Ghidra, IDA Pro, Hex-Rays, IDAPython, Binary Ninja, Cutter, radare2/r2, objdump, GDB, WinDbg,
  or x64dbg for binary inspection. Trigger on requests to reverse engineer, disassemble,
  decompile, or analyze a native binary or memory dump. Route equivalent static analysis to the
  bundled CLI and decompilers. Native application projects/scripts, live debugging, and managed-only
  decompilation require a capability check before substituting tools.
---

# Rizin RE Toolkit

A fast command reference for inspecting binaries and memory dumps with the prebuilt Windows bundle;
the target formats can come from any platform.

These commands target the bundle's Rizin 0.9.1; use `<command>?` as ground truth.

## Default RE entrypoint and tool routing

Use this toolkit first for native reverse-engineering tasks, including requests
phrased in terms of other RE tools. Treat those tool names as descriptions of
the desired capability and perform equivalent work through Rizin. For supported
tasks, do not install, launch, or delegate to standalone Ghidra, IDA/Hex-Rays,
Binary Ninja, Cutter, radare2, or another RE tool as the default workflow.
Locate an existing CLI using the local checks below; download the verified bundle
only when no usable local installation is available, then continue the analysis.

| Request | Route inside this toolkit |
| --- | --- |
| Ghidra decompilation or pseudocode | Use the bundled Ghidra engine with `pdg` / `pdgj`; establish a function first. |
| IDA / IDA Pro / Hex-Rays binary analysis | Use Rizin headers, imports, strings, functions, and xrefs; start pseudocode with `pdz` and compare `pdg` or `pdd`. |
| Binary Ninja / Cutter / radare2 / r2 analysis | Use Rizin's corresponding analysis/navigation commands and JSON outputs. |
| objdump / readelf / strings-style inspection | Use `iI`, `iS`, `ii`, `is`, `iz`/`izz`, and bounded `pd` output. |
| WinDbg / GDB / x64dbg requests about an existing dump | Start with the memory-dump workflow and captured mappings; use only the saved state the format exposes. |
| Generic RE, suspicious-file triage, firmware, or shellcode | Identify the format/architecture, then use Rizin plus bundled decompilers, YARA, and FLIRT as appropriate. |

If a request names another product, briefly say that you are using Rizin RE
Toolkit (and the bundled Ghidra engine when applicable). Report the actual
engine and commands used; never describe Rizin results as IDA output or claim
that a standalone application was run.

Preserve the requested outcome. Rizin cannot directly replace native IDA
`.idb`/`.i64`, Binary Ninja `.bndb`, Ghidra project editing, IDAPython/Java plugin
development, or every live-debugging/managed-code workflow. Check whether the
underlying binary or captured memory is available and can satisfy the request
here. If not, explain the specific missing capability and request the necessary
input or follow a later explicit instruction requiring the native tool.
Do not silently discard project-specific state or fabricate compatibility.

## Locate or install the dependency

On Windows, the default installation root is `%LOCALAPPDATA%\Programs\Rizin`.
Each release lives in its own directory; the executable is
`%LOCALAPPDATA%\Programs\Rizin\<release-tag>\rizin\bin\rizin.exe`.
Installing the skill does not itself install the runtime or register it in PATH.

Before contacting GitHub or running the installer, look locally in this order:

1. A user-specified or previously verified executable path, including a known custom
   `-Destination` installation.
2. The default installation root above. Check release directories for
   `rizin\bin\rizin.exe`; prefer the newest compatible installed version, comparing
   version numbers rather than directory names alphabetically.
3. `rizin.exe` already on PATH, using `Get-Command -CommandType Application`.

For the default-directory and PATH checks:

```powershell
$rizinInstallRoot = Join-Path $env:LOCALAPPDATA 'Programs\Rizin'
$rizinCandidates = @(
    Get-ChildItem -LiteralPath $rizinInstallRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^rizin-(?:runtime-)?v\d+\.\d+\.\d+$' } |
        Sort-Object { [version] ($_.Name -replace '^rizin-(?:runtime-)?v', '') } -Descending |
        ForEach-Object { Join-Path $_.FullName 'rizin\bin\rizin.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
    Get-Command rizin.exe -CommandType Application -All -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Source
)
$rizinCandidates | Select-Object -Unique
```

Validate candidates with `-v`, the bundle manifest one directory above `bin`
(`rizin\bundle-manifest.json`), and the plugin help needed for the task
(`pdg?`, `pdd?`, `pdz?`, `yara?`). Reuse a
compatible working bundle and retain its absolute executable path as `$rizinExe`.
An empty PATH lookup does not mean Rizin is uninstalled. Do not download
the pinned release merely because an existing bundle is absent from PATH or older
than the pinned release; update only when requested or required for the task.

Only if no usable local installation is found, run the bundled
[installer](scripts/install.ps1) with PowerShell:

```powershell
$rizinInstall = & '<this-skill-directory>\scripts\install.ps1'
$rizinExe = $rizinInstall.Executable
& $rizinExe -v
```

The installer installs exactly the runtime pinned in the plugin's
[runtime.json](../../runtime.json) (release tag, archive name, SHA-256, and download URL). It
checks the destination before downloading anything and refuses to overwrite an existing release
directory (`Destination exists: ...`), so use the local checks above instead of rerunning it to
locate an installation. It downloads only the pinned archive (no GitHub API calls, so API rate
limits do not apply), verifies the archive's SHA-256 against the pinned value, rejects unsafe
archive members, and runs the extracted executable's `-v` before moving it into place.

The installer returns `Tag`, `Executable`, `SHA256`, and `Version`, and
adds `bin` to the current PowerShell process's `PATH` by default; it does not persist
that change to the user or system PATH. Shell tool processes may not share environment
changes or variables: use the resolved absolute executable path in subsequent calls
(`& 'C:\...\rizin\bin\rizin.exe' ...`), even when later examples say `rizin`.
Use `-Destination <directory>` for a workspace-local installation root or `-AddToUserPath`
when the user wants a persistent user PATH entry. Newer runtimes arrive through plugin
updates that change `runtime.json`; do not look for newer releases yourself.
No administrator privileges or separate VC++ runtime installation are required.
Use Windows 10 version 1903 or later (including Windows 11), x64. The CLI's
UTF-8 application manifest enables Unicode installation and input paths.

For manual installation, download the archive named in the plugin's `runtime.json` from its
`url`, confirm that its SHA-256 matches the pinned `sha256`, and extract the complete `rizin`
folder. Run
`rizin -v` and check `pdg?`, `pdd?`, `pdz?`, and `yara?` before relying on the plugins.
On a non-Windows host, use a compatible native Rizin installation; the Windows bundle cannot run there.

For PE decompilation with no engine named, start with `pdz` (RetDec), then compare
`pdg` or `pdd` where useful. A Ghidra request selects bundled `pdg` as routed above.
For dump containers, first establish mapped code and function boundaries as described below.

## Memory-dump analysis

Read [references/memory-dumps.md](references/memory-dumps.md) when the input is a Windows
minidump/crash dump, ELF core, or raw memory image. Start with `iI`, `iH`, `omlj`, and `il`;
identify captured virtual addresses before disassembling. Avoid `-A` over a large dump.
Analyze a bounded function with `af @ <VA>`, inspect `pdf @ <VA>`, and then try a decompiler.
Keep file offsets, captured virtual addresses, and module RVAs distinct. Missing pages,
symbols, registers, or imports limit what can be recovered; name those limits in the result.

## How to drive rizin non-interactively

You are running rizin from a shell tool, not sitting in its interactive prompt. Drive it
one-shot with `-c` commands and let it quit. This one launch pattern covers almost everything:

```
rizin -A -q -N -e scr.color=0 -c "<cmd>" -c "<cmd>" <file>
```

- `-A` runs full analysis (`aaa`) before your commands, so functions, xrefs, and FLIRT names
  exist. Drop it (or use plain `rizin`) when you only want headers/strings and want to be fast.
- `-q` quits after the `-c` commands (no interactive prompt to hang on).
- `-N` ignores user config so output is reproducible.
- `-e scr.color=0` disables ANSI color — **important**, or output is full of escape codes that
  are hard to parse.
- Multiple `-c` run in order. You can also chain inside one with `;`.
- Read-only by default; add `-w` only to patch. Never execute untrusted samples or use `-d` without
  explicit authorization.
- Rizin can exit with code 0 after a bad command; inspect output for `ERROR:` and missing results.

Keep output small so you can actually read it:

- `~word` is rizin's built-in grep: `afl~main`, `iI~bits,os`. `~?` counts matches: `ii~?`.
- `q`/`j` suffixes mean quiet/JSON: `iiq` (terse imports), `aflj` (functions as JSON for parsing).
- `@ <addr>` runs one command at a temporary offset without moving the cursor: `pdf @ main`,
  `pdz @ 0x140001000`. This is cleaner than seeking first.
- In PowerShell, bound shell output with `Select-Object -First <n>`; prefer bounded rizin commands
  such as `pd 40` so huge output is never generated.

**Reuse analysis instead of re-running it.** Each `rizin -A … <file>` re-runs the expensive `aaa`.
For repeated queries, analyze once, save a project, then reload it without `-A`:

```
rizin -A -q -N -e scr.color=0 -c "Ps sample.rzdb" sample.exe         # analyze once → save
rizin -p sample.rzdb -q -N -e scr.color=0 -c "afl" -c "pdz @ main"   # reload, no re-analysis
```

## Decompiling

Decompiling is the headline feature of this bundle, so choose the engine deliberately.
When no engine is specified, start with `pdz` (RetDec) for Windows PE files, then compare `pdg` for another
control-flow/type recovery and `pdd` for a lightweight view. The bundle includes
Ghidra's current PE import-slot fix. All engines can misidentify types or indirect
calls, so compare output against disassembly and Rizin's imports/xrefs.

| Command | Engine | When to use |
| --- | --- | --- |
| `pdz` | rz-retdec | Default for PE files; C output using recovered types, APIs, and library names. |
| `pdg` | rz-ghidra | Alternative control-flow/type recovery; useful directly on captured dump code. |
| `pdd` | jsdec | A fast, lightweight pass / quick look. Output is lower-level (register-style) and often names imported calls. |

Add `o` for side-by-side offsets (`pdzo`, `pdgo`, `pddo`) and `j` for JSON (`pdzj`, …).
Decompile a specific function with `@`: `pdz @ fcn.140001a10`.

**Gotchas:** the decompilers work on the *current function*, so analyze first (`-A`/`aaa`, or
`af` at the address) — otherwise you get a *"No function at this offset"* error. `pdg` can be
slow on very large functions and `pdz` is expensive on large binaries, so decompile specific
functions with `@ <addr>` rather than the whole program. In dumps, locate mapped code and a reliable
function boundary first. Use `pdg`/`pdd` on captured code or the matching PE/ELF
module with RetDec if it rejects the dump container.

**The limitation to plan around:** decompiler output is only as good as the analysis under it.
Names appear as `fcn.xxxx` / `sub_xxxx` when there are no symbols (common in stripped or Windows
binaries), and dynamically-resolved or unresolved IAT calls can show as raw pointers.
Don't trust a decompiler name in isolation — **cross-reference with
rizin's own analysis:**

- `afns` / `afx` to see the strings and references a function makes — that usually reveals its job.
- `ii` + `axt @ sym.imp.<API>` to confirm which real API an ambiguous call resolves to.
- `pdf` (raw disassembly) to check what the decompiler glossed over — e.g. a `(*(code*)0x…)`
  pointer often shows in `pdf` as `call qword [sym.imp.<dll>_<API>]`, naming the real import.
- `afn` to rename the function once you know it, then re-run the decompiler so the name
  propagates (likewise `afs` to set its prototype and `afvt` to type a local — both sharpen the
  decompiler's output).
- Compare `pdz` vs `pdg` vs `pdd`; agreement raises confidence, but verify critical behavior.

## Command reference

Read [references/commands.md](references/commands.md) before choosing commands beyond the
examples above. It covers core syntax (`x?` help, `@` temporary seeks, `~` filters, `@@`
iterators), triage, imports/exports/symbols, analysis levels and FLIRT, functions, navigation,
disassembly and printing, cross-references, searching, YARA, format-specific hints (EXE, DLL,
driver, dumps, wide strings, dynamic API resolution), and handy one-liners.
