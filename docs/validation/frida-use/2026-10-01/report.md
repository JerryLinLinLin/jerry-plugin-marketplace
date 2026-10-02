# Frida Use integration and Windows experiments

Run date: **2026-10-01, America/Chicago**. Results are kept at repository level;
none of this report, the experiment harnesses, or captured results are included
under `plugins/frida-use`.

## Environment and evidence

- Windows 11 x64, build 26200.
- Python 3.13.15 x64, Frida 17.19.0, frida-tools 14.10.4.
- MSVC 14.51.36231, native x64 and x86 EXEs/DLLs compiled from the checked-in fixtures.
- [Integration results](integration.json): 25 passed cases.
- [CLI results](cli.json): 4 passed cases.
- [Relocation result](portable.json): 1 passed case.
- [Selected runner evidence](runner-evidence.json) and
  [source/binary SHA-256 manifest](artifact-hashes.json).

The 30 cases execute actual processes and hooks. Raw local captures remain under
`build/frida-use-run-05`, `build/frida-cli-run-02`, and `build/frida-portable-run`.
The retained JSON evidence is accompanied by the CLI transcripts in the same
validation directory. Reproduction commands and fixture source are in
[scripts/frida-use-experiments](../../../../scripts/frida-use-experiments).

## Observed behavior

| Experiment | Observed result |
| --- | --- |
| x64/x86 Ã— QJS/V8 spawn | Ready before resume, successful Unicode file-open events, one chosen API boundary, owned target cleanup |
| PID attach on both architectures | Capture completes and target remains alive after detach |
| Native hook + return modification | `demo_score(5)`: 12 â†’ 99 â†’ 12 after listener removal |
| Whole-function replacement | 12 â†’ 105 â†’ 12 after Interceptor.revert |
| Executable-memory byte patch | Private scratch function: 7 â†’ 42 â†’ 7; original six bytes restored |
| x86 stdcall / x64 win64 | SystemFunction returns value 0 with lastError 1234 |
| DLL observer | A newly loaded DLL is hooked, unloaded, reloaded and hooked again; native result remains 18 |
| V8 Inspector, both architectures | Debugger.paused arrives in debugProbe; resume completes the RPC with 42 |
| Binary messages | Exact 12-byte ReadFile buffer arrives; persisted attachment hash matches |
| Natural process exit | Process-terminated event and Windows exit code 0 |
| Native exception exit | Exit code 0xe0424242 is retained and controller returns failure even when Frida supplies no crash object |
| Startup JS error, syntax error, no-ready timeout | Failure is recorded; owned process is not resumed and no suspended child is left |
| Async JS failure | Error is recorded and owned spawn cleanup completes |
| Attachment cap | A 2 MiB attachment is saved as 1 MiB with original size and truncation metadata |
| Queue pressure | Dropped event count is nonzero and controller returns 2 |
| Real REPL and frida-trace | File hooks fire; trace generates ReadFile.js and emits timed ReadFile events on x64/x86 |
| Relocated plugin | A separate full copy under a path containing spaces and Chinese characters resolves its default agent and captures a live file-open event |

## Corrections found during experiments

1. An owned spawn could be killed before the asynchronous detached event arrived.
   Cleanup then attempted RPC on a destroyed script. Teardown now tracks the
   known-dead target and tolerates natural exit races.
2. KERNEL32 and KERNELBASE exported distinct addresses for a wrapper/callee pair.
   Address deduplication alone produced two events per logical file open. The
   default observer now chooses the KERNELBASE boundary when available.
3. On this Windows backend, an unhandled native exception produced a detached
   notification with no crash object. The runner now retains a read-only process
   handle and checks signaled process exit status separately.
4. The real frida-trace CLI returned 1 when its target terminated after tracing.
   The CLI experiment checks the emitted trace/termination behavior explicitly;
   the plugin explains that tool status and target exit status are different.

## Packaging checks

- Portable manifest validates against Agent Plugins 1.0.0 schema.
- Compatibility and portable OpenAI settings agree; both plugin and skill display
  names are Frida Use, and both IDs are frida-use.
- The skill-creator validator passes; maintained internal links resolve.
- All 31 source project files are preserved byte-for-byte and match the import
  manifest, with upstream license notices retained.
- No plugin submodule, nested Git checkout, symlink, junction or old source-path
  runtime dependency. The plugin works after relocation.

## Documentation language update

After the runtime experiments, all plugin content was standardized on English,
including source link labels and both plugins' skill descriptions. A full scan
of 65 plugin files found no Chinese characters; both skill validators, manifest
checks, and 48 maintained local links passed. The 31 imported files and executable
helpers retain their recorded hashes. The original artifact hash manifest above
remains a record of the experiment snapshot; documentation changes and their
current hashes are recorded in
[english-content-checks.json](english-content-checks.json).

## Interpreter-path follow-up

The README now saves the virtual environment's absolute Python path and uses it
for installation and every helper command, including after a directory change.
This was checked in a fresh isolated environment with Frida 17.19.0 and
frida-tools 14.10.4. From the skill directory, the environment's interpreter ran
the doctor successfully and captured four real file-open events from the native
fixture, exiting with code 0. See
[setup-interpreter-check.json](setup-interpreter-check.json).

## Limits

These experiments cover the supplied helpers and the stated native fixtures on
this environment. They do not establish compatibility with every Windows build,
ARM64/ARM64EC, remote server/Gadget/Barebone, protected processes, managed-runtime
bridges, real malware, live WinDbg/x64dbg co-attachment, hardware watchpoints,
Stalker coverage, TLS stacks, or child-gating process trees. Those guides cite
primary sources and require case-specific capability checks. No universal claim
of successful injection, atomic patching or complete behavior coverage is made.
