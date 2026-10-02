# Advanced workflows

## 1. Longer patches: LIEF, trampolines and rewriting

When replacement code exceeds the verified overwrite interval, do not truncate it, consume
the next instruction without analysis, or insert bytes into the middle of the file. Prefer
a source rebuild when source is available; otherwise choose a format-appropriate rewriter.

1. Define the new logic and return point. Confirm that the original site can hold a jump and
   enumerate incoming edges into overwritten instructions.
2. Allocate a valid, loadable code region with LIEF or a mature rewriter. Repeated zero bytes
   do not prove a code cave is unused. Check references, data, relocations, unwind metadata,
   loader use and permissions.
3. Relocate complete instructions and repair position-dependent operands. x64 RIP-relative
   instructions, calls/jumps, ARM literal pools and AArch64 ADRP pairs cannot simply retain
   their original encoding at a new VA. An assembler does not infer every relocation.
4. Handle jump range, registers, flags, stack state, ABI and exception/unwind information.
   Design any required veneer explicitly.
5. Write a new file, parse it again and inspect all structural changes. After any required
   signing/checksum step, test the intended behavior and normal paths.

LIEF's public Core supports format parsing and editing. Some disassembly and assembly APIs
belong to the **Extended** distribution; do not assume `uv add lief` provides them. This
plugin uses separate Capstone and Keystone engines. Builder APIs and rebuilding defaults
vary by format. Inspect the installed version's `help(...)` and upstream implementation,
write a minimal task script, then reread its output. A successful `write()` call is not
complete validation.

[Patcherex2](https://github.com/purseclab/Patcherex2) is an optional route for inserting code
or modifying functions. It has a target-platform matrix and assembler/analyzer/compiler
dependencies; it is not a universal PE/Mach-O replacement. Confirm the target and APIs,
pin dependencies in an isolated uv project, and follow the upstream InsertInstructionPatch
or ModifyFunctionPatch examples. Do not add it to every basic patching environment.

## 2. Malware analysis copies and deobfuscation

The purpose is clearer analysis, testing a deduction or limiting side effects in an experiment.
Preserve the original sample hash, unpacking/mapping information and evidence for every edit.
Use existing analysis tools for code discovery and decompilation; this skill handles located
changes.

- **Opaque predicates or constant branches:** establish the target through static conditions,
  symbolic reasoning or controlled traces. A trace for one input does not prove a branch is
  constant for all inputs. After confirmation, replace it with an appropriate direct branch
  or NOP sequence and record assumptions.
- **Indirect-branch simplification:** establish the target set and relevant register state.
  Preserve conditions when multiple targets remain possible. Assemble the branch at its
  actual VA and check instruction boundaries and incoming edges.
- **Writing decrypted code into an analysis file:** distinguish runtime addresses from file
  offsets and account for relocations and runtime repairs. An analysis-only output may not
  be executable; identify that limitation in its filename or report.
- **Controlling experimental side effects:** prefer explicitly modeled external functions
  and syscalls in emulation hooks. Removing one call does not make an entire sample safe to
  execute. If a stub returns a value, check its ABI, error paths and required state changes.

Decode before and after with Capstone and validate the edit with independent inputs or
traces. Do not execute unknown samples on the host Windows installation. This plugin's
experiments execute only the benign program compiled locally from readable repository source.

## 3. Differential validation with Unicorn

Unicorn is a CPU emulator, not a complete OS sandbox. Explicitly map code, stack and data;
initialize registers and entry state; bound instruction count and time. Model imports and
syscalls deliberately rather than forwarding unknown calls to host APIs. Treat the Thumb
execution-state bit separately from Capstone's even decode address.

Use identical initial registers and memory for before/after runs. Compare return values,
observable memory, flags and stack state as required by the contract. Include triggering,
boundary and normal inputs. Executing a single changed instruction does not establish
whole-function or process correctness. The plugin's `experiments/run_experiments.py` uses
`unicorn==2.1.4` and distinguishes emulated results from native execution.

## 4. File patches versus live process patches

This CLI writes disk copies only. Live changes require a target-platform debugger or
instrumentation tool, a verified process/module instance, and handling for ASLR, concurrent
threads, write permissions, atomicity, instruction-cache synchronization, existing breakpoints
and rollback. A request for a file patch does not imply attaching to a process or writing file
patch addresses directly into memory.

## 5. Distribute large-file deltas

Once the final file has passed behavior validation, use [xdelta3](https://github.com/jmacd/xdelta)
or [bsdiff/bspatch](https://github.com/mendsley/bsdiff) to reduce transfer size. These are separate
from Capstone. Obtain and verify native CLIs from their official releases; manage any Python
wrappers with uv.

```sh
xdelta3 -e -s original.bin final.bin update.vcdiff
xdelta3 -d -s original.bin update.vcdiff reconstructed.bin
```

Deliver the original file hash, final file hash, tool version, delta hash and supported input
version together. Reconstruct to a new path, require `SHA256(reconstructed) == SHA256(final)`,
and repeat validation with the target loader. Restore the preserved original for rollback,
or generate and verify a separate reverse delta. A distribution delta does not describe the
fix's intent or authenticate its source; it does not replace instruction review, signing
or correct version selection.
