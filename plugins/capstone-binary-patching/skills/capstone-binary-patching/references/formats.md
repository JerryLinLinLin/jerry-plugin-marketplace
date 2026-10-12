# Addresses, formats and architectures

## Address mapping

`info` reports size, SHA-256, format, machine type and file-backed mappings. `map` checks the
entire requested interval. Raw inputs require `--format raw --base-va ...`. `disasm` uses
the supplied offset and VA without automatically checking their relationship. Run `map`
first for executable files; do not invent an address merely because bytes decode there.

| Format | Translation and bounds |
| --- | --- |
| PE | `RVA = static VA - ImageBase`. Within the corresponding section, `offset = PointerToRawData + RVA - section.VirtualAddress`. The whole interval must fit actual raw data; a virtual tail has no disk bytes. The helper also excludes alignment padding beyond VirtualSize. |
| ELF | Use PT_LOAD: `offset = p_offset + VA - p_vaddr`, bounded by `p_filesz`, not `p_memsz`. BSS/zero-fill cannot be patched as existing file bytes. The helper accepts EXEC/DYN and rejects relocatable objects and core files. |
| Mach-O | Within a thin slice, `offset = segment.fileoff + VA - segment.vmaddr`, bounded by `filesize`. Fat files add slice offsets; the helper rejects fat inputs. Extract and identify a thin slice using appropriate platform tools first. |
| Raw | `VA = base_va + offset` applies only to a continuous, linear mapping. Containers, compressed streams and partition headers need separate handling. |

Translate runtime addresses back through loading changes. For PE, compute
`RVA = runtime VA - actual module base`, then add the original ImageBase for static VA.
For ELF, subtract the established load bias; for Mach-O, subtract the dyld slide. Do not
assume these are zero. The helper neither edits process memory nor automatically treats a
memory dump as an on-disk image.

If mappings overlap, a range crosses regions, a file is truncated or bytes have no file
backing, stop that write and correct the mapping or use structural editing. PE headers,
certificate tables and overlays are not ordinary section mappings; inspect the relevant
field definitions before editing them as data.

## Architecture-specific checks

| Architecture | Common pitfalls |
| --- | --- |
| x86/x64 | Variable-length instructions require a known decode boundary. Check short/near branches, rel32 range and RIP-relative operands. Moving original bytes often changes references. Preserve flags, stack alignment, calling conventions and unwind information. |
| ARM A32 | Instructions generally require four-byte alignment. Account for PC-relative addressing, conditional execution and literal pools. The helper's `arm` mode is little-endian A32 and does not infer ISA extensions. |
| Thumb | Address bit zero identifies execution state rather than a byte offset; give the helper an even code address. Instructions can be 16 or 32 bits. IT blocks and Thumb-2 branches require surrounding context. |
| AArch64 | Instructions require four-byte alignment. Check branch immediate range, ADRP+ADD/LDR page relationships, literal loads, BTI/PAC and the ABI. Trampolines may need veneers and register preservation. |
| MIPS32 | Establish little or big endian explicitly. Traditional branch delay slots cannot be ignored when patching. microMIPS, MIPS16 and R6 differ from ordinary MIPS32 and need a separately verified mode; the helper does not infer them. |

Use Capstone detail for structured operands, groups and register information. Do not rely
only on mnemonic text or assume `regs_read` lists every explicit and implicit dependency.
Backend detail completeness varies; check critical semantics against ISA documentation and
real execution or emulation.

## Verify format changes

Even an equal-length edit can invalidate a signature. Structural rewriting can also affect
layout, relocations, imports and exception metadata.

- PE: inspect section alignment, raw/virtual sizes, IAT/base relocations, CFG/CET/exception
  tables, checksums and Authenticode. Recomputing a checksum does not restore signature validity.
- ELF: inspect PT_LOAD offset/VA alignment, permissions, dynamic tables, relocations, GOT/PLT
  and unwind information. Adding a section alone does not make the loader map it; a valid
  segment is required.
- Mach-O: inspect load commands, `__LINKEDIT`, fixups/bindings, entry points and code signatures.
  Use the appropriate platform tools to rebuild fat slices and sign outputs; validate loading
  in the corresponding Apple environment.
- Firmware: handle container compression, checksums and signatures, then target ABI and
  hardware constraints. Editing an extracted component does not establish that a rebuilt
  image boots. Validate deployment only on the user's test target.

Parsing non-Windows formats on Windows does not verify Linux or macOS loader behavior.
