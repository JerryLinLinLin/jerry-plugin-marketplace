# Construct, apply and reverse patches

`<cli>` means the absolute path to this skill's `scripts/patchkit.py`. Commands read and write
artifacts in the current working directory and work in PowerShell or bash after replacing
placeholders. Successful commands exit 0; input or validation failures exit 2. JSON results
go to stdout and errors to stderr. The CLI never executes inputs. LIEF may also emit parser
diagnostics on stderr.

## 1. Change an immediate, branch or instruction range

Define the behavior to change and a corresponding test input, such as correcting a bad limit.
Use existing analysis to identify a function or basic-block entry, then inspect the mapping:

```sh
uv run --managed-python --locked <cli> info original.exe
uv run --managed-python --locked <cli> map original.exe --va 0x140001000 --size 5
uv run --managed-python --locked <cli> disasm original.exe --arch x86_64 --offset 0x400 --va 0x140001000 --size 5
uv run --managed-python --locked --with keystone-engine==0.9.2 <cli> assemble --arch x86_64 --va 0x140001000 --asm 'mov eax, 0x2a'
```

These addresses illustrate the command format; use values established for the actual input.
If the replacement is shorter than a verified interval of complete instructions, explicitly
request `--pad-to <length>` for architecture-specific NOP padding. Check incoming references
to the padded range. Padding does not enlarge the overwrite interval or relocate adjacent
instructions. ARM NOP encodings still require confirmation against the target ISA version.

Create a UTF-8 `spec.json`. A code patch's `before` and `after` must cover whole instructions,
not just the byte containing an immediate:

```json
{
  "format": "auto",
  "arch": "x86_64",
  "patches": [
    {
      "kind": "code",
      "offset": "0x400",
      "anchor_offset": "0x400",
      "before": "b807000000",
      "after": "b82a000000",
      "reason": "Change the verified return constant from 7 to 42"
    }
  ]
}
```

`anchor_offset` is an independently established function or basic-block boundary. Do not set
it to the patch offset merely to pass validation. The tool decodes from the anchor through the
end of the patch, checking instruction boundaries at both ends. The anchor and patch must
share one continuous executable file mapping, with a decode window no larger than 64 KiB.
If a function contains embedded data, select a closer verified basic-block boundary.

```sh
uv run --managed-python --locked <cli> plan original.exe --spec spec.json --out patch-plan.json
uv run --managed-python --locked <cli> verify original.exe --plan patch-plan.json --state original
uv run --managed-python --locked <cli> apply original.exe --plan patch-plan.json --out patched.exe
uv run --managed-python --locked <cli> verify patched.exe --plan patch-plan.json
uv run --managed-python --locked <cli> diff original.exe patched.exe
```

`plan` is a dry run: it writes the plan without changing the input. Review its
`instruction_preview`, reasons, addresses and SHA-256 hashes. `apply` recomputes the result
from the current input and creates output only after all edits validate. It refuses existing
output paths and reads the new file back to verify the write. Forced termination or power
loss can leave an incomplete new file; run `verify` before using it.

For a branch edit, also check both Jcc successors, branch distance, x86 short/near encoding
lengths, the source of flags and instruction boundaries at the destination. The assembler's
output and its decoded form are evidence, not proof that a branch destination is valid.
Successful assembly does not establish correct relocation or data addressing.

## 2. Patch data, strings and configuration

Use `kind: "data"` and omit `anchor_offset`. A data-only spec may omit `arch`. Equal lengths,
exact original bytes and the whole-file hash still apply. Check encoding, NUL termination,
field capacity and references for strings; check endianness, width, signedness and checksums
for numbers. Include padding explicitly and preserve file size.

Data edits can address unmapped content such as an overlay, but the CLI does not repair
format semantics. Editing an instruction's immediate remains a `code` edit covering the
entire instruction.

## 3. Firmware and raw code

Use `"format": "raw"` and `"base_va": "0x1000"` in the spec. `base_va` is the address
corresponding to offset zero of the entire input file. If the input has several different
load mappings, extract a verified region, record its container offset, and rebuild the
container according to its format. Do not select raw merely to bypass executable checks.

Supported helper modes are `x86`, `x86_64`, `arm`, `thumb`, `arm64`, `mips32le` and `mips32be`.
The ARM, Thumb and ARM64 options use little-endian instruction streams. Use a separate,
version-pinned and verified task script for other modes. This is the helper's scope, not
Capstone's complete architecture list.

## 4. Multiple edits, multiple files and failures

A `patches` array may contain several nonoverlapping code/data intervals. All original bytes
are checked against the same input snapshot; the fully patched copy is then decoded. Do not
create overlapping or order-dependent hunks. For multiple files, create a separate hash-bound
plan and output copy for each. The CLI does not implement a transaction across files.

When hashes or original bytes do not match, locate and analyze the code again and create a
new plan for that version. Do not edit a plan's hash to force it onto another file. The CLI
has no wildcard original bytes, automatic search-and-replace or `--force` option.

## 5. Behavior validation and rollback

For a program that is safe and authorized to execute, run the same inputs against the original
and patched copies. Verify both the intended change and normal paths. `verify` establishes
that bytes match the plan and checks mappings and instruction boundaries; it does not prove
that the program behaves correctly or loads successfully.

```sh
uv run --managed-python --locked <cli> revert patched.exe --plan patch-plan.json --out restored.exe
uv run --managed-python --locked <cli> verify restored.exe --plan patch-plan.json --state original
uv run --managed-python --locked <cli> diff original.exe restored.exe
```

A complete rollback has `changed_positions: 0` and matching SHA-256 hashes. If signing,
rebuilding or checksum changes follow patch application, the original plan's reverse operation
will reject the altered hash. Retain artifacts and plans for each step, or restore the original
backup. Output preserves ordinary permission bits, not ownership, ACLs, xattrs, timestamps
or signature validity.

## Plan format and limits

Plans contain `schema`, `tool_version`, `size`, `input_sha256`, `output_sha256`, a canonical
`spec` and a human-readable `instruction_preview`. Integers accept decimal or `0x` strings;
hex strings may contain whitespace. Application recomputes previews rather than trusting
stored ones. A plan is a reviewable change record, not a digital signature or source
attestation.

The tool reads entire files into memory. Extract a clearly identified component before
processing large disk images. Diff output limits the number of ranges and preview bytes;
when `ranges_truncated` or `preview_truncated` is true, do not claim every difference has
been individually reviewed.
