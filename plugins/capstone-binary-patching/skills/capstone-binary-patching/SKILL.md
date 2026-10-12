---
name: capstone-binary-patching
description: >-
  Create, inspect, validate and reverse native binary patches using Capstone plus a uv-managed
  CLI. Use for binary patching, instruction and byte diffs, patch porting, firmware edits,
  and analysis-only deobfuscation copies. Focus on patch construction and verification.
  General decompilation, initial malware triage, live debugging and managed-bytecode editing
  belong to their own tools.
---

# Capstone Binary Patching

Turn the requested behavior change into a reviewable, verifiable, reversible patch using a
skill and CLI. No MCP server is required. Capstone is a disassembly and instruction-analysis
engine implemented in C, with Python bindings. It does not decompile, assemble, rebuild
executable formats or write patches. Use Capstone to inspect instructions, Keystone to
assemble replacement bytes, LIEF to parse mappings, and [patchkit.py](scripts/patchkit.py)
to validate edits and write a new file.

## Start the task

1. Establish the intended behavior, input file, output location, architecture, bitness,
   endianness and ARM execution state from the request and existing analysis. Reuse verified
   functions and cross-references. Use existing analysis tools when decompilation or a global
   control-flow graph is needed.
2. Read [Setup and uv](references/setup.md) for installation or environment problems. Use
   **uv** for all Python installation, dependencies and execution, including temporary scripts
   and optional tools. Do not use system pip, bare Python commands or conda.
3. Resolve `scripts/patchkit.py` to an absolute path inside this skill and run
   `uv run --managed-python --locked <script> doctor`. Retain its version output. In the
   references, `<cli>` means this absolute path, independent of the caller's working directory.
4. Read only the references needed for the task:

| Goal | Workflow |
| --- | --- |
| Change an immediate or branch, insert NOPs, edit data, apply multiple edits, or roll back | [Patch construction and CLI](references/patching.md) |
| Inspect a patch, compare versions, or port an old patch | [Diffing and patch porting](references/diffing.md) |
| Handle PE, ELF, Mach-O, firmware, ASLR, VAs, RVAs or file offsets | [Mappings and architectures](references/formats.md) |
| Insert longer code, build trampolines, simplify analysis copies, patch a process, or distribute deltas | [Advanced workflows](references/advanced.md) |
| Check tool selection, APIs, or English and Chinese primary-source examples | [Research sources and design rationale](references/sources.md) |

## Patch invariants

- Begin with the file hash and format. Use `info` and `map` to establish actual file mappings.
  Record runtime VA, static VA, RVA and file offset separately; never use an address directly
  as a file seek offset without translating it.
- Decode instruction edits from a known function or basic-block boundary. Cover complete
  instructions and strictly decode both versions. Do not use `skipdata` to establish patch
  correctness. Successful decoding alone proves neither code discovery nor semantic validity.
- A `plan` requires original bytes, replacement bytes and a concrete reason. Code edits also
  require `anchor_offset`, a known instruction boundary. The generated plan binds the entire
  input and output with SHA-256. `apply` and `revert` revalidate the plan and write a new path.
  The CLI rejects unequal lengths, overlapping edits, wrong file versions, truncated
  instructions and existing output paths.
- Assemble branches and calls at the patch's actual VA. Check destinations, fallthrough,
  incoming edges into overwritten instructions, registers, flags, stack state, calling
  conventions and position-dependent addressing. `plan` does not recover a control-flow
  graph or relocate instructions automatically.
- Use an advanced workflow for file growth, import edits, new sections or segments,
  trampolines and relocations. Do not insert bytes into the middle of an executable file.
- Use `kind=data` only for actual data, never to bypass instruction checks. Firmware
  checksums, PE checksums and signatures, and Mach-O code signing require separate handling;
  this CLI does not repair them automatically.
- Continue authorized local patch-copy work without repeatedly requesting confirmation.
  Executing samples, replacing deployed files and modifying live processes are separate
  actions governed by the actual task authorization. Creating a patch does not imply them.

## Require behavior evidence

Run `verify`, then inspect the complete byte diff and confirm that changes stay within the
planned ranges. For programs the user has authorized you to execute, run the original and
patched copies with the triggering case, boundary inputs and normal cases. Record output,
exit status and relevant side effects. Prefer real execution over disassembly or unit tests
alone. Validate unknown or malicious samples only in an established isolated environment or
bounded emulation; do not execute them on the host merely to test a patch.

The plugin's `experiments/` directory contains a locally compiled fixture and a reproducible
workflow that invokes the full CLI, executes Windows EXEs and checks rollback hashes. See
the plugin README. Report CPU emulation and native OS execution separately.

Deliver the patched copy, plan JSON, original and resulting hashes, offsets and addresses,
before/after instructions, behavior results and rollback command. State unverified platforms,
execution paths, structural changes and signature status. If only static checks passed,
report exactly that.
