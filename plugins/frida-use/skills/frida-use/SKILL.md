---
name: frida-use
description: >-
  Use Frida for dynamic instrumentation, debugging, API tracing, Python/JavaScript
  agents, malware analysis, reverse engineering, and runtime patching, primarily
  on Windows. Trigger on Frida, frida-trace, live API hooks, or requests to observe
  or change a native process at runtime, including Windows debugging, hooking,
  instrumentation, and unpacking.
  Static-only decompilation and native debugger projects need their own tools;
  do not silently substitute Frida for an explicitly requested debugger.
---

# Frida Use

Turn a runtime question into a small, reproducible instrumentation experiment.
Windows user-mode x86/x64 is the main path. Check capabilities before assuming
Windows ARM64, managed runtimes, a remote device, or Barebone behaves the same.

## Start with the actual debugging layer

| Question | Use / read |
| --- | --- |
| Install/import failure, wrong interpreter, architecture | [Setup and versions](references/setup.md); run `scripts/doctor.py` with the intended Python |
| Python hangs, RPC fails, lost messages, early exit | [Python controller](references/python-controller.md); adapt `scripts/frida_session.py` |
| Break inside the injected JS, inspect variables | [Windows debugging](references/windows-debugging.md): V8 + Inspector |
| Native CPU stepping, SEH, hardware data breakpoints, crash dump | Same debugging guide: coordinate with WinDbg/x64dbg and preserve the user's chosen debugger |
| Quick API trace | [CLI tools](references/cli-tools.md) |
| Write a native hook / resolve an RVA / decode Windows data | [Hooking recipes](references/windows-hooking-recipes.md) and [JS API notes](references/js-api-cheatsheet.md) |
| Analyze a sample, unpacking, child processes, behavior timeline | [Malware analysis](references/malware-analysis.md) |
| Alter arguments, return values, functions, or instructions | [Runtime patching](references/patching.md) |
| Diagnose anti-analysis behavior | [Anti-debug analysis](references/anti-debug-bypass.md) |
| TLS, crypto, desktop app data-flow or parser tests | [Product security](references/product-security.md) |
| Why these choices / Chinese and English research | [Research and sources](references/research-sources.md) |

Read only the references relevant to the task. `references/legacy/` and
`docs/migration/` preserve the complete imported material for historical lookup;
they contain obsolete examples, not current execution instructions.

## Working sequence

1. Identify the target executable/PID, architecture, launch arguments, and one
   behavior to reproduce. Reuse the user's authorization and target selection.
   Run untrusted samples only in the analysis environment designated for them.
2. Record Python executable, `frida` and `frida-tools` versions separately.
   Local Windows injection needs no server. An isolated CLI installation does
   not make `import frida` available to every Python interpreter.
3. Choose attach for existing state; spawn for startup coverage. With Python:
   spawn → attach → register callbacks → load → agent ready → resume.
   Spawn does not guarantee observation before every loader/TLS initializer.
4. Resolve the address in the live process. For internal code use module base +
   verified RVA; a PE file offset is not an RVA. Record module path/base and
   the actual resolved address, including forwarded exports.
5. Start with one bounded observation hook. Confirm it fires, its ABI is correct,
   and the target still behaves normally before increasing coverage or patching.
6. Save evidence with PID/TID, timestamps, module + RVA, errors and truncation.
   Capture input at entry and successful output at completion. Treat async I/O
   completion separately. Clean up hooks, scripts, sessions, and owned processes.

## Executable starting points

Paths below are relative to this skill directory. Prefer an explicit Python
executable from the analysis project's environment.

```powershell
python scripts/doctor.py
python scripts/frida_session.py --pid 1234 --out ./capture --duration 30
python scripts/frida_session.py --spawn 'C:/lab/demo.exe' --out ./capture-startup --kill-on-exit -- --demo
frida -p 1234 -l ./hooks.js
frida -f 'C:/lab/demo.exe' -l ./hooks.js --runtime v8 --debug --pause
```

The runner defaults to the bundled `assets/windows-observe.js`: bounded file-open
observations with a ready handshake. Custom agents must send `{type: 'ready'}`
after installing their initial instrumentation. Use a fresh output directory.
It records JSONL and binary attachments, has finite waits and capture duration,
and never kills a PID supplied for attach. Failed startup cleans up its own
spawn; `--kill-on-exit` also kills its spawned target after a successful capture.

## Invariants that prevent misleading results

- Use current Frida 17 module-instance and pointer methods. Do not copy removed
  `Module.getExportByName(module, name)` or `Memory.read*` static APIs from archives.
- Keep invocation state on `this` using normal methods, not arrow callbacks.
  Bound and guard memory reads; retain allocated arguments for their real lifetime.
- Windows `BOOL`/`DWORD`/`LONG` remain 32-bit on x64; handles, pointers and `SIZE_T`
  follow pointer width. x86 WINAPI commonly needs `stdcall`; x64 uses its Windows
  ABI. Verify each prototype, including output pointers and ownership.
- Use a module observer for delayed DLLs and deduplicate resolved addresses.
  Avoid forced DLL loading, blocking waits, or reentrant native calls in loader
  callbacks merely to make a hook appear to work.
- Log JS errors and native crashes separately. A JS `debugger;` statement pauses
  GumJS, not a native source line. No hook hit is not proof of absent behavior.
- Treat patching as an experiment with baseline, exact preconditions, and rollback.
  Unloading an Interceptor hook and undoing a manual byte write are different.
- Do not infer complete kernel/syscall visibility, undetectable instrumentation,
  generic .NET/HotSpot support, or persistent PE patching from ordinary user-mode hooks.

When finishing, state what was observed, what changed, which paths were exercised,
where the evidence is, and which capabilities were not exercised. Keep case-specific
captures and experiment reports in the user's analysis workspace, outside this skill.
