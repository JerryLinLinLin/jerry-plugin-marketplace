# Capstone Binary Patching

Skill + CLI plugin for constructing, comparing, validating and reversing native binary patches.
Version **0.1.0**. Plugin and skill ID: **`capstone-binary-patching`**.
Invoke as **`$capstone-binary-patching`**; automatic discovery is enabled.
No MCP server, account connection or bundled Python runtime.

Capstone decodes instructions; optional Keystone assembles them; LIEF parses executable mappings.
The bundled CLI produces hash-bound plans and writes equal-length edits to new files.
It covers PE, linked ELF, thin Mach-O and explicitly mapped raw inputs. General decompilation
remains with the existing RE tools. Python, dependencies and execution are managed with **uv**.

The [skill](skills/capstone-binary-patching/SKILL.md) and its English workflow references cover:

- Instruction, branch, NOP and data patches; multi-hunk plans and exact rollback.
- Byte diffs, matched-function instruction diffs and cross-version patch porting.
- PE/ELF/Mach-O addresses, ASLR, ARM/Thumb/AArch64/MIPS firmware constraints.
- Longer patches and trampolines, analysis-only deobfuscation copies, CPU validation and delta delivery.
- [English and Chinese primary-source research](skills/capstone-binary-patching/references/sources.md).

## Install and run

The marketplace catalog includes **Capstone Binary Patching**. Publishing/installing this checkout
is separate from editing it; refresh the marketplace after this version is actually published.
For local CLI use, follow the [uv setup guide](skills/capstone-binary-patching/references/setup.md):

```powershell
$patchCli = 'C:\absolute\path\to\capstone-binary-patching\skills\capstone-binary-patching\scripts\patchkit.py'
uv python install 3.13
uv run --managed-python --locked $patchCli doctor
uv run --managed-python --locked $patchCli --help
uv run --managed-python --locked $patchCli info '.\original.exe'
```

Core packages are pinned to Capstone 5.0.9 and LIEF 1.0.0, with a script lockfile.
Assembly adds `--with keystone-engine==0.9.2`. See [patching](skills/capstone-binary-patching/references/patching.md)
for the spec schema and plan/apply/verify/revert commands. All writes use new, nonexisting paths.

## Reproduce the actual experiments

From the repository root, on Windows with existing MSVC x64 build tools and uv:

```powershell
$env:UV_CACHE_DIR = Join-Path (Get-Location) 'build\capstone-uv-cache'
uv run --managed-python --locked '.\plugins\capstone-binary-patching\experiments\run_experiments.py' --out '.\build\capstone-experiments\my-run'
```

Choose a new output directory each time. The harness compiles the readable [fixture.c](experiments/fixture.c),
executes the EXE, invokes the real CLI to locate/assemble/plan/apply/verify a patch, executes the patched
EXE, reverts it, and executes the restored EXE. It compares stdout, exit codes and whole-file hashes.
It builds its tiny Kernel32 import library locally and needs no CRT headers or downloaded binary fixture.

It also exercises CLI refusal paths, emulates seven instruction modes using Unicorn 2.1.4,
checks a conditional branch with three inputs, mixes code/data edits, and tests constructed ELF/Mach-O
mappings. `--emulation-only` omits the native Windows compilation step on other hosts; it is not an
OS loader test. The harness never executes arbitrary user-provided samples.

Evidence includes `summary.json`, `transcript.jsonl` with actual arguments/stdout/stderr/exit codes,
EXEs, raw fixtures, specs and plans. Generated files remain under ignored `build/`.
See the [validation report](../../docs/validation-capstone-binary-patching-v0.1.0.md) for the recorded run
and the limits of that evidence. These are end-to-end experiments, not mocked unit tests.

## Boundaries

The CLI does not recover CFGs, prove semantic equivalence, relocate stolen instructions, grow files,
attach to processes, repair signatures/checksums or execute inputs. Advanced references explain
when to use additional tools. A decodable patch still needs behavior validation on its intended platform.
Base CLI is designed for Windows/Linux/macOS Python environments; native validation here is Windows x64.

Plugin code and instructions are [MIT](LICENSE). Dependencies keep their upstream licenses and are
downloaded by uv rather than redistributed inside this plugin.
