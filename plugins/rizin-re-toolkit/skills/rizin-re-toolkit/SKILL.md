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
Download the verified CLI below when it is missing, then continue the analysis.

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

## Install the dependency

Use an existing verified bundle when available. Otherwise run the bundled
[installer](scripts/install.ps1) with PowerShell on Windows:

```powershell
& '<this-skill-directory>\scripts\install.ps1'
```

The installer finds the newest stable Rizin bundle among the repository's
[GitHub releases](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/releases), verifies
`SHA256SUMS` and the GitHub asset digest when available, then extracts into
`%LOCALAPPDATA%\Programs\Rizin\<release-tag>\rizin`. It returns the executable path and
adds `bin` to the current PowerShell process's `PATH`. Shell tool processes may not share
environment changes: retain the returned absolute executable path for subsequent calls.
Use `-Destination <directory>` for a workspace-local install, `-Tag rizin-v0.3.1` for the
tested release, or `-AddToUserPath` when the user wants a persistent user PATH entry.
No administrator privileges or separate VC++ runtime installation are required.
Use Windows 10 version 1903 or later (including Windows 11), x64. The CLI's
UTF-8 application manifest enables Unicode installation and input paths.

For manual installation, download `rizin-windows-x64-bundle-v*.zip` and `SHA256SUMS` from
the same release, verify the archive hash, and extract the complete `rizin` folder. Run
`rizin -v` and check `pdg?`, `pdd?`, `pdz?`, and `yara?` before relying on the plugins.
If the repository is renamed, `-Repository owner/new-name` overrides the download source.
On a non-Windows host, use a compatible native Rizin installation; this release cannot run there.

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

## Core syntax (worth knowing)

| Syntax | Meaning |
| --- | --- |
| `x?` | Help for command `x` (e.g. `i?`, `ax?`, `pdg?`). Your ground truth when unsure. |
| `x @ addr` | Run `x` once at `addr` (temporary seek). |
| `xj` / `xq` / `x*` | JSON / quiet / "emit as rizin commands" variants when `x?` advertises them. |
| `cmd~grep` | Filter `cmd` output (`~?` = count, `~[2]` = column 2, `~:0` = first line). |
| `cmd @@ii` | Run `cmd` over every import; also `@@is` (symbols), `@@iz` (strings), `@@b` (basic blocks). |
| `cmd @@f:glob` | Run `cmd` over flags matching a glob (e.g. `@@f:str.*`). `@@F:glob` = functions. |
| `cmd @@c:cmd2` | Run `cmd` at every address in `cmd2`'s output. |

## Triage: what is this file?

| Command | Purpose |
| --- | --- |
| `iI` | Headers at a glance: arch, bits, OS, subsystem, compiler, stripped?, PIE, signed?, PDB path. |
| `i` / `ia` | Quick info / full summary of everything. |
| `iS` / `iR` / `iV` / `iC` / `iT` | Sections, resources, version data, signature info, and file hashes (`j` for JSON where supported). |
| `il` | Linked libraries or captured modules/drivers — often identifies a file or dump quickly. |
| `is` / `is~?` | Symbols (and a count). |
| `iz` / `izz` | Strings in data sections / in the **whole** file (`izz` catches more, e.g. packed). |
| `iz~?` | Just the string count — cheap way to gauge a binary. |
| `oml` / `omlj` | Captured virtual-memory maps and permissions in MDMP, DMP64, and ELF core files. |
| `iH` | Detailed headers; on DMP64, bugcheck parameters and triage/bitmap metadata. |
| `ar` / `arj` | Saved crash registers for DMP64 and supported ELF cores (`ar PC`, `ar SP`). |

`iI` needs no analysis, so run it with plain `rizin -q -N -e scr.color=0 -c iI <file>` for speed.

## Imports, exports, symbols

| Command | Purpose |
| --- | --- |
| `ii` / `iiq` | Imports (what the binary calls in other modules). The fastest read on behavior. |
| `iE` / `iEq` | **Exports** — the entry points of a DLL. Start here for a `.dll`. |
| `is` | All symbols (imports, exports, locals the binary kept). |
| `ir` | Relocations. |

Imports drive triage: `CreateProcessW` + `WriteFile` + `InternetOpenW` suggests capability before
you read an instruction. In a sufficiently complete MDMP, these commands aggregate captured PE
modules. To find *where* an import is used, cross-reference it (below).

## Analysis levels

| Command | What it does |
| --- | --- |
| `aaa` (= `rizin -A`) | The standard. Functions, calls, data refs, autonaming, **and applies FLIRT signatures**. |
| `aa` | Lighter: only symbols and entry. |
| `aaaa` | Experimental, more aggressive. Try it on stripped/obfuscated binaries when `aaa` misses functions. |
| `aap` | Recover functions by scanning for prologs (when calls don't reach them). |
| `Fl` / `Fa` / `Fs` / `Ff` | List/apply sigdb signatures, apply a chosen FLIRT file, or show the current FLIRT match. |

After `aaa`, FLIRT can name statically-linked library/CRT functions `flirt.*` instead of `fcn.*`.
Verify implausible names or boundaries in disassembly.

Do not start a large dump with `-A`: inspect `oml` first and target mapped code. For broader
dump analysis, constrain it with `e analysis.in=io.maps.x`.

## Functions

| Command | Purpose |
| --- | --- |
| `afl` | List all functions. `afl~name` to filter. |
| `aflt` | Function **table**: size, xrefsTo, xrefsFrom, calls, basic blocks, cyclomatic complexity. Great for spotting the big/central functions to look at first. |
| `afi` | Detailed info on the current function. |
| `afns` | Strings referenced by the current function — a one-line summary of what it probably does. |
| `afn <name> [@ addr]` | **Rename** a function. Do this as you recover meaning; the name then shows up in decompiler output and at every call site. |
| `afv` / `afvn <new> <old>` | List / rename local variables and arguments. |

## Navigation

| Command | Purpose |
| --- | --- |
| `s <addr\|flag>` | Seek (e.g. `s main`, `s entry0`, `s sym.imp.…`, `s fcn.140001000`). |
| `ieq` / `iej` | List actual program entrypoints; use this when `entry0` was renamed or is absent. |
| `s entry0` | Seek the conventional entry flag when present; for a `.sys`, this is effectively `DriverEntry`. |
| `ar PC` / `ar SP` | Read the saved crash PC/SP in DMP64 or a supported ELF core, then inspect that address. |
| `sf.` | Seek to the start of the current function. |
| `sh` / `shu` / `shr` | Seek history / undo / redo. |

Often you don't need to seek at all — just append `@ <addr>` to a print/decompile command.

## Disassembly & printing

| Command | Purpose |
| --- | --- |
| `pdf` | Disassemble the whole current function (`pdf @ main`). The workhorse. |
| `pd <n>` | Disassemble n instructions (negative = before). `pd 40 @ addr`. |
| `pdr` | Disassemble recursively over the function's basic-block graph. |
| `pdsf` | Function summary: strings, calls, jumps, and refs without the full listing — fast triage. |
| `px <n>` | Hexdump n bytes. `pxr` annotates words with refs (good for reading the stack/IAT). |
| `ps` / `psw @ addr` | Print a string at the address — `ps` (UTF-8, null-terminated) or `psw` (UTF-16LE = Windows wide strings). |

## Cross-references (the key to stripped code)

When functions are unnamed (`fcn.*`), xrefs are how you recover meaning.

| Command | Purpose |
| --- | --- |
| `axt [@ addr]` | Refs **to** an address — who calls/uses this. `axt @ sym.imp.KERNEL32.dll_CreateProcessW` lists every caller of that API. |
| `axf [@ addr]` | Refs **from one instruction/address**. |
| `afx [@ function]` | Aggregate references from a whole function (`afxj` for JSON). |
| `axt @@ii` | Refs to **every import** at once — a map of which functions touch which APIs. |
| `axt @@f:str.*` | Refs to every string — find the code that uses a telling string. |

Typical move: find an interesting string or import → `axt` to the function using it → read it
with `pdz` → `afn` it a real name → follow `afx`/`axt` outward.

## Searching

| Command | Purpose |
| --- | --- |
| `/z <text>` | Search for a string. |
| `/x <hexpairs>` | Search for raw bytes (`/x 4889e5`). |
| `/a <asm>` | Assemble an instruction and search its bytes (`/a "jmp rax"`). |
| `/R [filter]` | List ROP gadgets, optionally filtering their printed instructions. |

## YARA (rz-libyara)

| Command | Purpose |
| --- | --- |
| `yaral <file.yar>` | Load a `.yar`/`.yara` file, apply its rules, and flag every match. |
| `yarad <folder>` | Same, recursively over a folder of rules. |
| `yaraM` / `yaraMj` | List all matches found (plain / JSON). |
| `fs yara.match; fl` | Switch to the `yara.match` flag space and list the match flags (then `s` to one). |

Match flags distinguish physical (`yara.match.pa.*`) from virtual (`yara.match.va.*`) addresses;
use the matching `io.va=false`/`true` setting when reading bytes at a flag.

## Format-specific hints

- **EXE** — start with `iI` (subsystem GUI/console, compiler, is it signed, PDB path), then
  `ii` for behavior and `iz`/`izz` for strings. `s entry0` is CRT startup; the real logic is
  usually a few calls in — or seek `s main` if present.
- **DLL** — the exported functions *are* the API surface: list them with `iE`/`iEq` and
  decompile the interesting ones (`pdz @ sym.<export>`). `il` shows its own dependencies.
- **Driver (.sys)** — `iI` shows `os native` / `subsys Native`; `il` reveals the type
  (`fltmgr.sys` ⇒ file-system minifilter, `ndis.sys` ⇒ network, `ntoskrnl.exe` ⇒ core kernel).
  `entry0` is `DriverEntry`; from there follow calls to the dispatch/registration routines.
- **Windows MiniDump (`mdmp`)** — `iS`/`il` list modules; `oml` shows captured pages. Imports,
  exports, symbols, strings, and VA reads depend on those pages being present.
- **Windows kernel/crash dump (`dmp64`)** — use `iH`, `oml`, and `ar`. For richer postmortem
  inspection, open `dmp://C:/path/file.dmp`, then `e cfg.debug=1; dl dmp; dpa`; use `dr`, `dp`,
  `dpT`, `dmm`, and `dbt`. `dl dmp` may contact `pdb.server` to fetch symbols.
- **ELF core** — `oml` lists `PT_LOAD` maps; `ar` exposes supported saved registers. Rizin identifies
  the stack map on x86/x64/ARM/AArch64 cores.
- **Raw memory** — supply assumptions explicitly: `-F any -a <arch> -b <bits> -m <base>`;
  modules, threads, and discontiguous mappings are not inferred.
- **Wide strings** — Windows `…W` APIs use UTF-16; `iz`/`izz` detect them, and `psw @ addr`
  prints one (use plain `ps` for ASCII/UTF-8).
- **Dynamic API resolution** — `LoadLibrary`/`GetModuleHandle` + `GetProcAddress` patterns
  (common in packers/malware) hide real calls from the import table. Spot them via `afns`
  (the resolved API names appear as referenced strings) and confirm in the disassembly.
- **Find dangerous capability fast** — `axt @ sym.imp.KERNEL32.dll_CreateProcessW`,
  `…_WriteProcessMemory`, `…_VirtualAllocEx`, crypto/`advapi32` APIs, networking
  (`ws2_32`/`wininet`) — jump straight to the code that uses what you care about.

## Handy one-liners

```
# Headers only, fast (no analysis)
rizin -q -N -e scr.color=0 -c iI sample.exe

# Imports + strings overview
rizin -q -N -e scr.color=0 -c "iiq" -c "izzq" sample.dll

# Analyze, then list functions as a table
rizin -A -q -N -e scr.color=0 -c aflt sample.exe

# Decompile one function (RetDec is the default; pdg / pdd are alternatives)
rizin -A -q -N -e scr.color=0 -c "pdz @ 0x140001a10" sample.exe

# Who calls a risky API?
rizin -A -q -N -e scr.color=0 -c "axt @ sym.imp.KERNEL32.dll_CreateProcessW" sample.exe

# Export list for a DLL
rizin -q -N -e scr.color=0 -c iEq sample.dll

# Memory-dump overview (no full analysis yet)
rizin -q -N -e scr.color=0 -c "iI" -c "omlj" -c "ilq" sample.dmp
```

When a command's exact form is unclear, ask rizin: append `?` (e.g. `pdg?`, `i?`, `ax?`).
