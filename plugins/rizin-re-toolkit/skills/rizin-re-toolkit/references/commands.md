# Rizin command reference

These commands target the bundle's Rizin 0.9.1. When a form or suffix here is in doubt,
`<command>?` (for example `pdg?`) is ground truth.

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
