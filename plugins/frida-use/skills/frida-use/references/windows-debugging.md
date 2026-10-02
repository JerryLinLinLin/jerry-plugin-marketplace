# Windows debugging: choose the layer

| Layer | Useful tools | What a breakpoint means |
| --- | --- | --- |
| Python controller | IDE debugger / `python -m pdb controller.py` | Pauses the host controller, possibly delaying resume/RPC delivery |
| Injected JS/TS agent | Frida V8 Inspector / Chrome DevTools | Pauses GumJS execution; the native hook's thread can be blocked |
| Native target | WinDbg/x64dbg, Frida hooks, scoped Stalker | CPU state, native instructions, exceptions, memory access |

The same apparent hang can originate in any of these layers. First reproduce
with an empty agent, then one hook, then argument reads, then tracing/patches.
Keep the original target input and an uninstrumented baseline.

## Debugging the injected script

```powershell
frida -f 'C:/lab/demo.exe' -l ./hooks.js --runtime v8 --debug --pause
```

Use the printed Inspector endpoint (the usual default is port 9229). Open
`chrome://inspect`, configure the local endpoint if needed, and open the dedicated
DevTools. Set a breakpoint in a hook, then issue `%resume` in the Frida REPL and
exercise that API. A `debugger;` statement in the hook is another precise stop.

With Python, create the script with `runtime='v8'`, call
`script.enable_debugger(port)` and keep the session alive. Use an explicit port
to avoid defaults drifting and conflicts. The bundled runner exposes
`--runtime v8 --debug-port 9230`. Its normal deadlines still apply; increase them
deliberately for a long debugging session.

Do not expect an initial top-level breakpoint to wait for DevTools automatically.
Connect before evaluating the code of interest, or use a hook/RPC entrypoint to
trigger it after connecting. `--pause` controls the spawned program's main thread,
not a universal wait-for-JS-debugger option. TypeScript requires a compatible
bundle/source map; Python `create_script` does not compile arbitrary TS by itself.

When paused inside a native callback, avoid an Inspector expression that calls
into a subsystem holding a lock on that thread. Resume the Inspector before
waiting for RPC or unloading the script. Keep the endpoint local to the analysis
environment.

Sources: [Frida CLI debugging](https://frida.re/docs/frida-cli/),
[Xialuohun's Windows/Inspector walkthrough](https://xialuohun.top/posts/windows%E7%9B%B8%E5%85%B3/fridawindows/).

## Native state and symbolication

Inside a normal `onEnter` method:

```javascript
const frames = Thread.backtrace(this.context, Backtracer.ACCURATE);
send({type: 'backtrace', tid: this.threadId, caller: this.returnAddress.toString(),
  frames: frames.map(address => {
    const module = Process.findModuleByAddress(address);
    return {address: address.toString(), symbol: DebugSymbol.fromAddress(address).toString(),
      module: module === null ? null : module.name,
      rva: module === null ? null : address.sub(module.base).toString()};
  })});
```

Use `this.context` to unwind the intercepted native frame. Symbols may be absent
or mismatched; keep raw addresses and module/RVA data. `ACCURATE` depends on unwind
information; `FUZZY` may identify non-return-address values as frames. Neither
proves a complete call graph. PDB identity must match the binary.

For WinDbg, record the module identity/RVA and set a breakpoint such as
`bp demo+0x1234` after verifying that exact build. For deferred symbolic breakpoints
use the debugger's own module/symbol facilities. Avoid transplanting a live VA
from a different ASLR run. Frida can discover interesting call sites and supply
addresses; use the native debugger for register stepping and crash analysis.
Corelan's 2026 example demonstrates this handoff to WinDbg.

Concurrent debuggers can interfere: a native all-thread break may stop the agent
and make RPC time out; software breakpoints or two hooks on the same prologue may
collide. First confirm each tool independently, then combine a small scope. Do not
claim every tool pair or attach order works. If the user specifically requests
WinDbg/x64dbg, preserve that workflow.

Sources: [Corelan Frida-to-WinDbg workflow](https://www.corelan.be/index.php/2026/04/17/debugging-windbgx-automation-scripting-part-1/),
[Microsoft x64 ABI](https://learn.microsoft.com/en-us/cpp/build/x64-calling-convention?view=msvc-170).

## Exceptions and memory breakpoints

`Process.setExceptionHandler(details => ...)` observes a native exception through
Frida. Return `false` to leave it to the normal chain unless you have actually
handled it. Returning `true` without fixing PC/state can repeatedly fault or
hide the original crash. Do not blanket-suppress access violations.

`Thread.setHardwareBreakpoint` and `setHardwareWatchpoint` are capabilities on
thread objects in current Frida. Check method availability/backend, slot limits,
size/alignment and thread coverage. Pair the handler with an explicit plan to
unset/re-arm and continue; a breakpoint left at the same PC can immediately fire
again. Another debugger may own those debug registers. Use a native debugger
when the task needs reliable step/resume control rather than writing a new one.

`MemoryAccessMonitor` reports the first access to each monitored page, not every
read/write of a variable. It is not equivalent to a precise hardware watchpoint.
Stalker follows selected threads and translated code, not all process activity.
Restrict a time interval/function, filter modules, batch summaries, then unfollow
and collect garbage after pending callbacks drain. Self-modifying code and
unwinding through instrumentation require special care and a current core.

See the [JS API](https://frida.re/docs/javascript-api/) and
[Stalker internals](https://frida.re/docs/stalker/).

## Failure diagnosis

| Symptom | Distinguishing experiment |
| --- | --- |
| Script loads, no hooks fire | Prove the code path executes; verify PID, worker, resolved address, DLL timing, forwarded export and hook success |
| Failure before ready | Save JS error/stack; remove reads/native calls; verify installed API version |
| Correct logs, process crashes | Remove the mutation; verify ABI, memory lifetime, output size and reentrancy |
| Hangs after Python breakpoint | Check whether Python must still resume spawn or serve a message |
| RPC times out at JS breakpoint | Resume Inspector before awaiting RPC; do not run RPC in message callbacks |
| Native debugger stops, Frida stalls | Resume target threads before further agent operations |
| Hook hits twice | Check alias/forwarded addresses and duplicated observers/listeners |
| Missing output/decrypted data | Check success convention and async completion, not just function return |
| Backtrace ends at trampoline | Check matching symbols/unwind support and current Frida release; keep raw evidence |

If an empty-agent attach itself fails, collect the exact error, versions and target
identity before changing instrumentation. Do not default to disabling system
security controls, rewriting Frida for stealth, or swallowing exceptions.
