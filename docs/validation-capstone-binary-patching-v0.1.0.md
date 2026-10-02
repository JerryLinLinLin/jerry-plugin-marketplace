# Capstone Binary Patching 0.1.0: Local Validation

Validation date: 2026-10-01. Plugin: [Capstone Binary Patching](../plugins/capstone-binary-patching).
This record comes from local compilation, execution and real CLI invocations. Behavior
validation was performed against actual artifacts and CPU emulation.

## Environment and reproduction

| Component | Recorded environment |
| --- | --- |
| Host | Windows 11 x64, 10.0.26200 |
| uv | 0.11.18 |
| Python | uv-installed and managed CPython 3.13.13, under workspace `build/capstone-python` |
| Base dependencies | capstone 5.0.9, lief 1.0.0 |
| Assembler / CPU emulator | keystone-engine 0.9.2 / unicorn 2.1.4 |
| Compiler | MSVC 14.51.36231, Hostx64/x64 |
| Final run | `build/capstone-experiments/run-03`: 98 subprocess commands and 13 passing result groups |

Run from the repository root, choosing an output directory that does not exist:

```powershell
$env:UV_CACHE_DIR = Join-Path (Get-Location) 'build\capstone-uv-cache'
$env:UV_PYTHON_INSTALL_DIR = Join-Path (Get-Location) 'build\capstone-python'
uv python install --no-bin --no-registry 3.13
uv run --managed-python --locked --python 3.13 '.\plugins\capstone-binary-patching\experiments\run_experiments.py' --out '.\build\capstone-experiments\new-run'
```

The [experiment harness](../plugins/capstone-binary-patching/experiments/run_experiments.py)
invokes the plugin's actual CLI entry point. Base and experiment scripts have separate uv
locks; the optional Keystone dependency is supplied through a version-pinned `--with`.
The first two rounds also exercised uv environments using an existing CPython 3.13.15
installation. The final round explicitly used uv-managed Python.

## Native Windows program execution

MSVC compiled the EXE locally from [fixture.c](../plugins/capstone-binary-patching/experiments/fixture.c).
A locally generated Kernel32 import library provides console output and process exit; no test
EXE was downloaded. The exported `patch_target` supplies the function RVA. The CLI maps its
VA to a file offset, decodes with Capstone, assembles replacement bytes with Keystone, creates
a plan and writes the patched copy.

| Artifact | Actual stdout | Actual exit code |
| --- | --- | --- |
| original.exe | `result=07` | 7 |
| patched.exe | `result=42` | 42 |
| restored.exe | `result=07` | 7 |

Patch location: file offset `0x400`, static VA `0x140001000`. The complete instruction changes
from `b8 07 00 00 00` (`mov eax, 7`) to `b8 2a 00 00 00` (`mov eax, 0x2a`). Only one byte in
the entire EXE changes, at offset `0x401`; the patch hunk still covers the whole instruction.

```text
original SHA-256  80f8a1646fdd8c1ba25c8b54cdda378d0d8344cc8e8be9004825569ebf41afba
patched  SHA-256  7e1de8e6ffd14cb25f9fa25d7559d3ae5de8dcd07f9fc0b65b238dea33a6be8a
restored SHA-256  80f8a1646fdd8c1ba25c8b54cdda378d0d8344cc8e8be9004825569ebf41afba
```

These hashes identify the final recorded build. Another compilation may produce different
hashes because of PE build metadata. Each run requires its original and restored files to
match byte for byte and verifies that the original remains unchanged.

## CPU behavior and format experiments

| Scope | Observed result |
| --- | --- |
| x86, x86_64, ARM, Thumb, AArch64, MIPS32 LE and MIPS32 BE | Each mode passes CLI assemble, plan, apply, verify and revert. Unicorn register values are 7, 42 and 7 respectively, with matching rollback hashes. |
| Conditional branches with absolute targets | Code assembled at VA `0x8000` is exercised with inputs 6, 7 and 8. Original results are `[1,7,1]`, patched results `[1,1,7]`, and rollback restores `[1,7,1]`. |
| Multiple code/data edits | One plan corrects a comparison immediate and changes `OLD\0` to `NEW\0`; both edits apply, verify and revert together. |
| Address-dependent jump and NOP padding | A jump from `0x8000` to `0x8010`, padded to five bytes, produces `eb0e909090`. |
| ELF and Mach-O | Constructed mapping fixtures pass actual LIEF parsing, bidirectional mapping and CLI patch/verify/revert. Their mapped code yields 7, 42 and 7 in Unicorn. |

The ELF/Mach-O fixtures test parsing, file mapping and instruction execution, not native
Linux/macOS loading. CPU emulation does not establish complete syscall, dynamic-linking,
ABI or signature behavior.

## Exercised failure paths

The real CLI returned 15 expected refusals with exit code 2, covering:

- A different build, an already patched file and tampering outside the patch interval.
- Output targeting the original file or an existing patched file.
- A patch starting inside an instruction, truncated original instructions and truncated replacements.
- Unequal patch lengths, overlapping hunks and an architecture inconsistent with the PE header.
- ELF/Mach-O zero-fill without file backing, a fat Mach-O without slice selection, and partially overlapping ELF file mappings.

Rejected patch/plan commands did not create their requested artifacts. Existing original and
patched files retained their hashes.

## Raw evidence and implementation versions

Complete local records:

- [summary.json](../build/capstone-experiments/run-03/summary.json): group results, actual versions, interpreter paths and implementation hashes.
- [transcript.jsonl](../build/capstone-experiments/run-03/transcript.jsonl): argv, stdout, stderr and exit codes for 98 commands.
- [pe-plan.json](../build/capstone-experiments/run-03/pe-plan.json): the actual patch plan and before/after instructions.

The `build/` artifacts remain on this machine; binaries and caches are not committed. The
reproduction harness and this report are retained in source control.

```text
patchkit.py SHA-256
b17596f094d9e97aefff9032b39befc9b0333e38fc479894c05694685b7d0c31
run_experiments.py SHA-256
1ef14edf0b4ae13fa13a60db9a0a30061b984465f3d7b0b601a6c8b3513b088a
fixture.c SHA-256
d5cf1a8b4510af87eef58c37df6d666fca2e626066a6cb439b0eb8507a2382bf
```

## Additional checks and limits

The skill-creator `quick_validate.py` check passed, using `PYTHONUTF8=1` on Windows. Python
static error checks passed. The portable manifest was validated against the official Agent
Plugins 1.0.0 JSON schema; additional checks covered compatibility-manifest consistency,
marketplace paths, automatic-discovery metadata and local documentation links.

Capstone's distribution version is 5.0.9, while the binding's `__version__` reported 5.0.7.
`doctor` records both alongside the native core version. Decoding, assembly and execution
passed without modifying the upstream package to conceal this discrepancy.

The experiments do not validate native loading on other host platforms, every path through
complex real programs, signature/checksum rebuilding, longer-code rewriting, live process
patching, or installation/execution of QBinDiff, Patcherex2 and xdelta. Those are explicitly
bounded advanced or optional workflows, not implemented base-CLI features. The plugin has
been added to the local marketplace catalog; no GitHub release or installation into the
current Codex global plugin cache has been performed.
