# Capstone Binary Patching

Create precise native binary patches and verify exactly what changed. The plugin combines instruction analysis with a CLI for planning, applying, checking, and reversing edits while preserving the original input.

## What it helps with

- Compare bytes and instructions before and after a change.
- Build instruction, branch, NOP, and data patches, including multi-part edits.
- Port patches between versions and check address mappings across PE, ELF, Mach-O, and raw inputs.
- Verify patched bytes and restore the original file with exact rollback.

Capstone decodes instructions, LIEF interprets executable mappings, and optional Keystone support assembles replacements. The [agent workflow](skills/capstone-binary-patching/SKILL.md) explains when additional analysis or platform testing is needed.

## Install and use

Install **Capstone Binary Patching** from **Jerry's Plugin Marketplace** and follow the [setup guide](skills/capstone-binary-patching/references/setup.md). Python and dependencies are managed with **uv**; no MCP server or account connection is required.

Invoke `$capstone-binary-patching`, or ask your agent to inspect a proposed patch. For direct CLI use:

```powershell
$patchCli = 'C:\path\to\capstone-binary-patching\skills\capstone-binary-patching\scripts\patchkit.py'
uv run --managed-python --locked $patchCli doctor
uv run --managed-python --locked $patchCli --help
uv run --managed-python --locked $patchCli info '.\original.exe'
```

Dependency versions are managed by the script metadata and lockfile. See the [patching guide](skills/capstone-binary-patching/references/patching.md) for assembly setup and the plan, apply, verify, and revert workflow.

## Validation and boundaries

Patches are written to new files. Successful decoding does not prove equivalent behavior: validate each change on its intended platform. The CLI does not attach to processes, execute inputs, grow files, relocate instructions, or repair executable signatures.

The [experiment harness](experiments/run_experiments.py) builds a controlled Windows fixture and checks its behavior before patching, after patching, and after rollback. To run it from the repository root with MSVC x64 tools and uv:

```powershell
uv run --managed-python --locked '.\plugins\capstone-binary-patching\experiments\run_experiments.py' --out '.\build\capstone-experiments\my-run'
```

Choose a fresh output directory. The harness records commands, output, exit codes, and hashes; it also checks instruction emulation and format mappings. See the [reference guides](skills/capstone-binary-patching/references) for platform limits and advanced workflows.

Plugin code and instructions are [MIT](LICENSE). Dependencies retain their upstream licenses.
