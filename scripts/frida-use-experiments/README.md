# Frida Use maintainer experiments

These experiments run real Frida instrumentation against locally compiled, owned
Windows native fixtures. They are kept outside the distributed plugin and skill.
They do not use mocked Frida APIs. Build outputs and raw captures go under the
repository's ignored `build/` directory; reviewed evidence is retained under
`docs/validation/frida-use/`.

Requirements: Windows x64, MSVC x86/x64 build tools, Python with `frida`,
`frida-tools` and `websockets`. The Inspector experiment uses the actual V8
DevTools protocol: enable debugging, invoke an RPC with `debugger;`, observe
`Debugger.paused`, resume, and verify the RPC's result.

From the repository root, choose **new** output directories for each run:

```powershell
& ./scripts/frida-use-experiments/build-fixtures.ps1 -OutputDirectory ./build/frida-use-fixtures
python scripts/frida-use-experiments/integration.py --fixtures ./build/frida-use-fixtures --out ./build/frida-use-capture
python scripts/frida-use-experiments/cli-experiments.py --fixtures ./build/frida-use-fixtures --out ./build/frida-use-cli
```

The fixture performs bounded file I/O to its supplied test path, provides native
functions for patch/ABI checks, and exits. The crash case deliberately raises a
noncontinuable application exception in that fixture only. Each subprocess is
owned by the experiment and cleaned up. No malware sample is required.

The integration script covers x64/x86 × QJS/V8, ready/resume ordering, Unicode
paths, attach survival, binary fidelity, Windows error state, delayed DLL reloads,
function/byte patch reversal, Inspector, normal exit, native exception exit,
startup JS/syntax/ready failures, queue pressure and attachment bounds.
The separate CLI experiment launches `frida` and `frida-trace` directly and checks
their real output/generated handlers. On frida-tools 14.10.4 a traced target's
natural termination yields tool exit 1; that is explicitly checked, not hidden.

See [the retained run report](../../docs/frida-use-validation.md) for the executed
environment, evidence and remaining coverage limits. These results are not part
of the installed skill's instructional material.
