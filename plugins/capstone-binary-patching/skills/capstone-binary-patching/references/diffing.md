# Diffing and patch porting

## Choose the comparison first

| Question | Method | What it establishes |
| --- | --- | --- |
| Which bytes changed after patching a specific input? | Whole-file hashes and a positional byte diff | Changed offsets, lengths and unexpected edits |
| Which instructions changed in already matched functions or blocks? | Capstone decoding, byte/operand comparison and control-flow inspection | Observable instruction differences, not semantic equivalence |
| Which functions correspond across rebuilt versions? | Symbols, control-flow/call graphs and reference features; optionally QBinDiff | Candidate matches with confidence that still require review |

Capstone is not itself a binary differ. Whole-program function matching, relocation
normalization and similarity scoring need additional logic. For packed or compressed inputs,
first establish comparable representations. An unpacked memory image and an on-disk file do
not necessarily share offsets.

## 1. Audit a newly generated patch

```sh
uv run --managed-python --locked <cli> diff original.exe patched.exe --limit 128
uv run --managed-python --locked <cli> verify patched.exe --plan patch-plan.json
```

Inspect lengths, input/output hashes, `changed_positions` and whether every changed range
belongs to the plan. Replacing a five-byte instruction may change only one byte; the code
patch hunk should still cover the whole instruction.

If output is truncated, raise `--limit` up to 10000 or write a bounded comparison script that
records all ranges. `diff` compares fixed offsets. A one-byte insertion can make many later
positions differ without implying equally extensive semantic changes.

## 2. Compare matched functions

Establish each side's function entry, file offset, VA and exact code length independently:

```sh
uv run --managed-python --locked <cli> diff-code old.exe new.exe --arch x86_64 --old-offset 0x400 --old-va 0x140001000 --old-size 6 --new-offset 0x600 --new-va 0x140002000 --new-size 6
```

The two ranges are strictly decoded independently and may have different lengths. Output
retains both sides' addresses, bytes and instructions plus a unified diff. Addresses here
are examples; each decode range is limited to 64 KiB. Comparison lines omit instruction
addresses but retain encoding and operands, so relative-call encodings and absolute branch
target changes still appear. Relocation differences are not automatically hidden.

If normalization is useful, create a separate matching view and retain the exact diff:

- Use instruction details to distinguish registers, ordinary constants, control-flow targets
  and memory references. Do not remove every number with a regular expression.
- Internal branch targets can become basic-block identifiers, and imported calls can become
  symbols. Consult relocation records before treating an address as variable.
- Preserve error codes, size limits, bit masks and arguments. Removing them can conceal the
  actual fix.
- Inspect added/deleted blocks, comparisons and exception paths. Matching mnemonics do not
  establish matching behavior.

## 3. Port an old patch to a new version

Prefer having old-original, old-patched, new-original and the old patch's purpose. Recover
the old change with an exact diff before finding corresponding code in the new version.

1. Prefer symbols and debug information. For stripped files, combine distinctive strings,
   imported calls, neighboring blocks and call-graph context.
2. Byte patterns produce candidates. Restrict searches to the intended module, section or
   function and record zero, one or multiple matches. Even a unique match needs architecture,
   reachability, semantic, instruction-boundary and context checks.
3. Assemble at the new function's actual VA. Recompute branches, RIP-relative references,
   ADRP sequences and other position-dependent operands. Do not copy the old file offset,
   displacement or plan hash.
4. Create a fresh spec/plan for new-original. Check whether the new version already fixes the
   behavior or changes its ABI or logic.
5. Validate the triggering case and normal inputs. If the patch no longer applies, state the
   evidence rather than forcing it.

For extensive function movement, consider [QBinDiff](https://github.com/quarkslab/qbindiff):

```sh
uv tool run --from qbindiff qbindiff --help
uv tool run --from qbindiff qbindiff -o matched.bindiff old.BinExport new.BinExport
```

These optional upstream commands are outside the base lock and local validation scope.
Before first use, check the chosen version's wheel/native-build requirements, record its
version and pin it. Inputs are supported exports such as BinExport or Quokka; passing two
arbitrary EXEs is not a complete analysis pipeline. Results depend on export and analysis
quality, and matching large graphs can require substantial memory.

For Mach-O, [macho-diff](https://github.com/NohamR/macho-diff) demonstrates a useful combination
of LIEF, Capstone, function hashes and instruction comparison. Its pattern patches still
need target-version uniqueness, complete-instruction and expected-byte checks. This plugin
does not automatically download or execute that project.

## 4. Separate analysis diffs from distribution deltas

To efficiently update known file A into known file B, use xdelta or bsdiff rather than
aligning disassembly text yourself. To understand or port a fix, a delta file alone cannot
explain its semantics. See the advanced workflow for delta delivery.
