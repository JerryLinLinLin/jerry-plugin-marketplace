# Setup, dependencies and uv

## Install the prerequisites

Reuse an existing `uv` installation. Otherwise follow the
[Astral installation instructions](https://docs.astral.sh/uv/getting-started/installation/).
For example, the official standalone installer on Windows is:

```powershell
powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
```

On macOS or Linux:

```sh
curl -LsSf https://astral.sh/uv/install.sh | sh
```

These commands install uv itself. Follow the host's execution permissions; use an approved
installer or mirror in restricted environments. Verify `uv --version` in a fresh shell, or
use the absolute executable path returned by the installer.

```sh
uv python install 3.13
uv run --managed-python --python 3.13 --locked /absolute/path/to/scripts/patchkit.py doctor
```

The script's PEP 723 metadata pins `capstone==5.0.9` and `lief==1.0.0`. Its adjacent
`.py.lock` records distributions and hashes. uv creates an isolated environment without
manual activation or changes to the target project's dependencies. Supported Python versions
are 3.11 through 3.14; see the validation report for the tested version. Dependency baseline
checked on 2026-10-01. `uv tool install capstone` does not install this CLI: Capstone's Python
package is a library. Use `--managed-python` so uv also supplies the interpreter.

In PowerShell, retain the script path in a variable:

```powershell
$patchCli = 'C:\absolute\plugin\skills\capstone-binary-patching\scripts\patchkit.py'
uv run --managed-python --locked $patchCli doctor
uv run --managed-python --locked $patchCli --help
```

Keystone is needed only for assembly:

```powershell
uv run --managed-python --locked --with keystone-engine==0.9.2 $patchCli assemble --arch x86_64 --va 0x140001000 --asm 'mov eax, 0x2a'
```

Use the PyPI package **keystone-engine**, not the unrelated OpenStack package named `keystone`.
The optional `--with` dependency has an explicit version but is not part of the base script
lock. Supply it for each invocation that needs it. `doctor` reports packages available in
that invocation's environment. Basic patching and diffing need neither Keystone nor Unicorn.

## Write task-specific scripts

For LIEF rebuilding, Unicorn validation or other helpers, declare PEP 723 metadata in a
workspace script and execute it through uv. Include only the dependencies that script needs.

```python
# /// script
# requires-python = ">=3.11,<3.15"
# dependencies = ["capstone==5.0.9", "lief==1.0.0"]
# ///
import capstone
import lief
```

```sh
uv lock --script task.py
uv run --managed-python --locked task.py
```

Inside an existing uv project, use `uv add capstone==5.0.9 lief==1.0.0` followed by
`uv run --managed-python ...` only when the task belongs to that project. `uvx` is suitable
for packages with CLI entry points, rather than directly executing a library.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| uv cache is not writable | Set `UV_CACHE_DIR` to a writable workspace cache. Set `UV_PYTHON_INSTALL_DIR` separately if the interpreter installation must also stay in the workspace. |
| User bin links or Windows registry registration are unnecessary | On Windows, use `uv python install --no-bin --no-registry 3.13`, then `uv run --managed-python ...`. The interpreter can remain in a designated writable directory. |
| No wheel matches the host | Distinguish host CPU/OS from target architecture. Check the published wheels; Keystone 0.9.2 does not cover every modern ARM host. Keep the base CLI available and use a verified LLVM/native assembler, or build the upstream library separately. |
| Capstone or Keystone native library cannot load | Check the wheel's DLL/.so/.dylib, interpreter bitness and `LIBCAPSTONE_PATH`. Do not copy an incompatible global library into the environment. |
| `capstone.__version__` reports 5.0.7 | This was observed with the PyPI 5.0.9 distribution. Report `importlib.metadata.version('capstone')` alongside binding/core version strings and the decode smoke test. |
| Offline execution | Prepare the uv interpreter and cache on the same host platform, then use `uv run --managed-python --offline --locked ...`. Missing cached artifacts cause failure; the plugin does not bundle an offline runtime. |
| Dependencies need updating | Update script metadata, run `uv lock --script ...`, and repeat the relevant end-to-end experiments before committing the lockfile. |

Build Capstone from native source only when wheels cannot support the host or engine development
requires it. Follow its GitHub build instructions for the C toolchain. uv manages Python but
does not install MSVC, system SDKs or every native dependency.
