# Environment, versions, and target selection

Use one explicit Python interpreter for installing packages and running a controller.
The CLI distribution is `frida-tools`; `import frida` comes from `frida`. Their
version numbers are independent. The reference point for this guide is
Frida 17.19.0 / frida-tools 14.10.4 (2026-10-01); record the actual environment
instead of assuming that a future install has these versions.

## Python projects

```powershell
python -m venv .venv
& ./.venv/Scripts/python.exe -m pip install frida-tools
& ./.venv/Scripts/python.exe -c 'import sys, frida; print(sys.executable, frida.__version__)'
& ./.venv/Scripts/python.exe -m pip show frida frida-tools
& ./.venv/Scripts/frida.exe --version
& ./.venv/Scripts/frida-ps.exe
```

Activation is optional. Explicit executable paths avoid PowerShell execution-policy
changes and IDE interpreter mismatches. Capture `pip freeze` once a useful project
environment works; do not continually upgrade it during a reproduction.

For just CLI tools, an isolated uv tool is convenient:

```powershell
uv tool install frida-tools
uv tool dir --bin
uv tool list
```

Use the reported bin directory. Do not assume a fixed user path. A uv tool
environment does **not** install Frida into system Python or your project venv.
For a controller with uv, use a project environment (`uv venv`, then
`uv pip install --python ./.venv/Scripts/python.exe frida-tools`) and run that
interpreter. Change persistent PATH only when that is part of the user's setup.

Sources: [Frida installation](https://frida.re/docs/installation/),
[uv tool isolation](https://docs.astral.sh/uv/guides/tools/).

## Architecture and access

- The Python wheel must match the **host interpreter** architecture. The injected
  agent must support the **target** architecture. A normal Windows x64 Frida
  distribution can instrument x86/WOW64 as well as x64 targets; a 32-bit target
  does not automatically require reinstalling everything as 32-bit Python.
- Inspect `Process.arch` and `Process.pointerSize` inside the target. Do not derive
  target pointer sizes from `platform.machine()` or Python's `struct.calcsize`.
- For ARM64/ARM64EC, emulated processes, custom builds, remote servers, or old
  Windows versions, check the release assets and backend support. Do not extrapolate
  x64 behavior. Frida 17.12 and later include Windows ARM64-specific fixes.
- Start with a process owned by the current user. If access is denied, verify PID,
  session, integrity level, architecture, target lifetime and endpoint logs.
  Elevation can address an integrity mismatch; it does not grant universal access
  to protected/PPL processes. Do not recommend disabling host protections as setup.

Source: [Frida Windows/ARM64 changes](https://frida.re/news/2026/06/10/frida-17-12-0-released/).

## Local, remote, and Gadget

Local Windows: `frida -p 1234` injects directly; no adb, USB switch, or server is
needed. `frida-ps -a` is application enumeration, not a portable substitute for
listing Windows processes.

Remote analysis: run the appropriate Windows server in the analysis machine,
use the same Frida core release at both ends, and verify the exact version with
`frida --version` and the server's `--version`. `frida-tools` has its own version.
Prefer a loopback endpoint reached through a controlled tunnel. For example,
after configuring an SSH tunnel to the analysis machine's loopback server:

```powershell
frida-ps -H 127.0.0.1:27042
frida -H 127.0.0.1:27042 -p 1234 -l ./hooks.js
```

If using built-in TLS/token options, verify `--help` on both ends. Keep server
access restricted: this is a process-control interface, not a public service.
Do not copy inconsistent port numbers from tutorials.

Gadget embeds instrumentation into a controlled application. Its architecture,
configuration filename, startup interaction and `on_load` behavior are a separate
setup; placing a DLL beside an EXE alone does not guarantee it is loaded.
See [operation modes](https://frida.re/docs/modes/) and [Gadget](https://frida.re/docs/gadget/).

## Diagnosing import/installation failures

Run the bundled `scripts/doctor.py` using the intended interpreter. A successful
import is a package check; `frida-ps` checks the local device backend; attaching
to a disposable program tests actual injection. Distinguish these stages.

| Symptom | Next useful check |
| --- | --- |
| CLI works, `import frida` fails | Compare interpreter path with CLI shim and installation environment |
| `_frida` DLL import error | Read the exact loader error; check wheel tags/architecture, damaged files, dependencies and security logs before prescribing a runtime |
| pip starts a native source build | Check supported Python/wheel combination and index; prefer a matching official wheel |
| Ambiguous process name | Select an explicit PID and verify its path |
| No local process / short-lived launcher | Track the launched worker's PID and lifetime |
| Connection/protocol error | Confirm local vs remote device and core/server versions |

Avoid old `.egg`/`easy_install` recipes, arbitrary third-party binaries, guessed
package constraints, or the assumption that every DLL failure is the VC++ runtime.
