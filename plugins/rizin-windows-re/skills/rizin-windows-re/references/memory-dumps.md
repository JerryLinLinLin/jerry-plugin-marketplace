# Memory dumps with Rizin 0.9.1

Keep the original dump read-only. Record its SHA-256, format, architecture, and
capture completeness. A snapshot cannot recover uncaptured memory, all original
source names, or execution history.

## Identify the container and captured addresses

```powershell
rizin -q -N -e scr.color=0 -c "iI" -c "iH" -c "omlj" -c "il" sample.dmp
```

The extension alone is insufficient. `iI` identifies the parser, `iH` gives
headers, `omlj` lists mappings, and `il` lists modules when supported. Use `iS`
for sections and `is`/`ii`/`iE` for recovered symbols/imports/exports. Empty
results may mean those pages were not captured.

| Format | Useful evidence | Limits |
| --- | --- | --- |
| Windows minidump (`mdmp`, `MDMP` magic) | Modules, captured memory, PE imports/exports when their pages exist | Thread/context streams vary; do not assume `ar` reconstructs every MDMP thread. |
| Windows x64 kernel/crash dump (`dmp64`) | `iH` for bugcheck data, `omlj` for maps, `ar` for supported registers | Small/triage/bitmap/full dumps capture different amounts of memory. |
| ELF core | `PT_LOAD` mappings and supported register notes with `omlj` and `ar` | Register support depends on architecture and OS. |
| Raw memory region | Bytes at a caller-supplied base | No automatic process/module/thread or discontiguous physical-memory reconstruction. |

Start with ordinary `rizin sample.dmp`. Live-process `-d` is unnecessary. The
optional `dmp://` backend is a separate postmortem debugger: consult `dl?`/`dp?`
before using it. Loading symbols through it can contact `pdb.server`; make any
network symbol retrieval explicit.

## Select and analyze a bounded function

Locate a relevant module or executable captured range. Start from a known
symbol, a supported saved instruction pointer, export, or disassembled call
target. Replace this example with a VA that actually exists in `omlj`:

```powershell
rizin -q -N -e scr.color=0 -c "px 64 @ 0x140001000" `
  -c "pd 24 @ 0x140001000" -c "af @ 0x140001000" `
  -c "pdf @ 0x140001000" -c "pdg @ 0x140001000" sample.dmp
```

A crash PC may be in the middle of a function: establish its start before
`af`. Follow `axt`/`afx` after analysis and compare `pdd` when useful. Try `pdz`
when RetDec accepts the container; otherwise use Ghidra/jsdec on captured code,
or obtain the matching original PE/ELF module for RetDec. A mapped memory image
is not a disk PE: section layouts and imports differ.

For broader analysis, inspect permissions and set `e analysis.in=io.maps.x`
before `aaa`. Some dump maps lack execute permissions; target addresses
explicitly in that case. Avoid unconditional `-A` on multi-gigabyte dumps.
Recover meaning from instructions, strings, calls, and xrefs; verify important
decompiler claims with `pdf`.

## Addresses, ASLR, and missing pages

- With virtual addressing enabled, `pd`, `px`, and `@` use mapped VAs. A file
  offset inside the dump is a different coordinate.
- Module RVA = captured VA minus the module's captured load base. Use this
  RVA to locate the same function in the matching disk binary, accounting for
  its own preferred base and section layout.
- Use a separate session with `io.va=false` to inspect file offsets. Do not
  silently reinterpret physical YARA matches as VAs.
- Unreadable/zero-filled output does not prove the process contained zeros.
  Confirm that a range was captured before interpreting its bytes as code.
- For a contiguous raw region, supply the known architecture, bitness, and base:

```powershell
rizin -F any -a x86 -b 64 -m 0x140000000 -q -N -e scr.color=0 `
  -c "px 64" -c "pd 24" memory-region.bin
```

This models one region, not an entire physical-memory image with page tables.
Comprehensive process, handle, or kernel-object reconstruction needs additional
format-aware memory-forensics tooling.

## Strings, rules, and reporting

Prefer bounded strings/searches and module-specific ranges before scanning an
entire image. `yaral <rules.yar>` applies local rules; `yaraMj` reports matches.
`yara.match.pa.*` and `yara.match.va.*` distinguish file and virtual addresses.
A rule match is evidence to inspect, not a verdict.

Report the dump hash/format, module and VA/RVA for each finding, supporting
instructions or decompiled code, and missing-page/symbol limitations. Separate
hypotheses from observed bytes. Use `Ps <project.rzdb>` to preserve analysis.

Release smoke tests cover a real Windows minidump of the fixture's own process
and a synthetic raw region. Kernel dumps and ELF cores rely on upstream
parsers and are not claimed as covered by these tests.
