# Analyze anti-debug and anti-instrumentation behavior

Use this when a program changes behavior under analysis. The inherited detailed
encyclopedia is under `legacy/anti-debug-db/`; its OS/version assumptions and
sample snippets are historical. It is not an automatic list of hooks to install.

## Distinguish causes

| Mechanism | Evidence to collect | Common misinterpretation |
| --- | --- | --- |
| IsDebuggerPresent / CheckRemoteDebuggerPresent | Call site, returned BOOL, output BOOL where applicable | CheckRemoteDebuggerPresent's return is API success, not debugger presence |
| NtQueryInformationProcess | Class, status, output size and buffer contents | All information classes have the same structure or success output |
| PEB / heap flags / direct reads | Exact build/architecture, reading instruction and flags | One x64 offset or old heap layout applies to WOW64 and every Windows release |
| Debug registers / thread context | Thread and context flags, debugger-owned slots | A clean main thread means every thread is unmonitored |
| Timing / waits | Repeated baseline and instrumented measurements | Any delay difference proves a deliberate check |
| Exceptions / SEH / VEH | First-chance vs unhandled flow and handler order | Suppressing the exception preserves intended behavior |
| Integrity / module scans | Actual compared region, hash or enumeration result | A universal Frida string/port/thread-name rule fits every build |

Ordinary local Windows injection does not require a listening frida-server.
Changing port 27042 cannot solve a detector that inspects local code modifications.
Frida instrumentation itself can alter timing, memory layout, prologues, module
lists and exception behavior; the absence of an OS debugger flag does not mean
there is no instrumentation.

## Controlled intervention

For the user's analysis target, identify one specific check that prevents the
desired observation. Confirm its ABI, inputs, outputs and return contract. Make
the narrowest temporary change that tests the hypothesis, retaining the baseline
and rollback. Prefer changing the identified check/caller in the test copy over
global patches to unrelated system behavior.

For example, CheckRemoteDebuggerPresent has both a success return and a BOOL
output. Merely forcing its return to zero describes failure, not "no debugger".
Likewise NTSTATUS information queries need consistent status, buffer contents and
returned length. Avoid generic success patches whose output invariants are unknown.

Do not blanket-skip Sleep/QPC/RDTSC or erase heap flags: programs may depend on
those clocks and structures, and the change can create a different failure.
For exception-based checks, native debugger visibility is often more useful
than adding another exception handler. Continue to record the original path.

A research skill should not promise undetectable Frida, recommend turning off
host security products as a routine fix, or rebuild tooling for stealth simply
because an attach failed. First test an empty agent, permissions, exact versions
and the offending call path.

Sources: [Check Point Anti-Debug-DB](https://github.com/CheckPointSW/Anti-Debug-DB),
[CheckRemoteDebuggerPresent](https://learn.microsoft.com/en-us/windows/win32/api/debugapi/nf-debugapi-checkremotedebuggerpresent),
[IsDebuggerPresent](https://learn.microsoft.com/en-us/windows/win32/api/debugapi/nf-debugapi-isdebuggerpresent).
