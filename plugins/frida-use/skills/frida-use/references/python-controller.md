# Python controllers that survive real sessions

Python controls the session; injected JavaScript runs inside the target. Keep
agent code in a UTF-8 `.js` file and load it with an explicit script name. A Frida
agent is not a Node.js process: filesystem/modules/bridges require the Frida API
or a compatible compiled bundle. Frida 17 moved runtime bridges out of bare GumJS;
a script that works in the CLI may still need bridge imports and compilation
when loaded through Python.

## Lifecycle and readiness

Use the bundled [frida_session.py](../scripts/frida_session.py) as the local
single-target runner. Its model is:

```text
read agent → prepare fresh output → select local device
  attach: attach explicit PID
  spawn:  spawn executable + argv → attach spawned PID
register detached/message/log callbacks → create/load named agent
wait for agent ready → resume only a spawn → capture until deadline/exit/error
dispose RPC (if present) → unload → detach → flush evidence
```

An agent should signal readiness after installing its initial hooks:

```javascript
rpc.exports = {
  status() { return {pid: Process.id, arch: Process.arch}; },
  dispose() { /* remove observers/listeners and restore any manual patches */ }
};
send({type: 'ready', pid: Process.id});
```

`script.load()` can return even though an agent reported a top-level JS error.
Do not treat a successful load call or a fixed sleep as proof of readiness.
An async agent needs an explicit ready message once its prerequisites are met.
If it is waiting for a DLL that can only load **after** resume, install a module
observer first, signal that the observer is ready, then resume. Otherwise the
controller and target can wait for each other forever.

Keep strong references to device/session/script for their required lifetime.
Register `session.on('detached', callback)` and record its reason and optional
crash. A native process termination, transport failure, destroyed script and
JavaScript exception are distinct failures.

Frida's optional crash object is not guaranteed on every backend. For a local
Windows process, retain a query/synchronize handle and inspect its exit status
after the process object is signaled. The runner does this independently of
Frida's crash notification. A nonzero code is an abnormal result to investigate,
not automatically proof of a crash; the application may choose that code itself.
If query access is unavailable, preserve that visibility gap.
See [GetExitCodeProcess](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getexitcodeprocess).

## Messages, binary data, and callback threading

`script.on('message', on_message)` receives `(message, data)`. Inspect the envelope:

- `type == 'send'`: user payload in `message['payload']`, optional binary in `data`.
- `type == 'error'`: retain `description`, `stack`, filename and line/column if present.
- Console output uses the script log handler; save it separately from application events.

Send bytes as `send(metadata, pointer.readByteArray(length))`. Do not stringify
megabytes, cast pointers to imprecise JS numbers, or decode arbitrary bytes as
UTF-8. Send addresses/UInt64 values as strings; distinguish a null attachment
from a zero-length attachment. Compute hashes on the host and assign filenames
there, never from untrusted target-provided paths.

Callbacks may run on Frida's delivery thread. Enqueue work and return promptly:
no `input()`, long disk writes, sleeps, or synchronous RPC back into the same
script. Such RPC can deadlock its own delivery. For async applications use a
thread-safe handoff such as `loop.call_soon_threadsafe`, not direct cross-thread
mutation of asyncio objects. Bound queues and report lost events.

The bundled runner uses a 128-entry queue, a 1 MiB per-attachment limit and a
64 MiB total attachment budget. It records original vs saved sizes and hashes.
These bounds protect host-side capture; the agent must still bound the original
memory read and event rate. JSONL grows with agent messages, so filter hot hooks
at their source. Exit 2 reports queue overflow; inspect summary/truncation even
when exit is 0. Do not describe a truncated capture as complete.

Source: [Frida messaging](https://frida.re/docs/messages/).

## RPC, recv and cancellation

```javascript
rpc.exports = {
  describeTarget() { return {pid: Process.id, base: Process.mainModule.base.toString()}; }
};
```

```python
# From the controller thread, with the script already loaded:
info = script.exports_sync.describe_target()
```

Python maps the exported camelCase name to snake_case. Prefer explicit
`exports_sync` or the installed binding's async interface over legacy ambiguous
`script.exports`. Handle RPC errors, disposed scripts and process termination.
Frida 17.16 introduced generated bindings and `frida.aio`; inspect the installed
version before transplanting old asyncio recipes.

`recv(type, callback)` consumes one message; re-register when implementing a
stream. `recv(...).wait()` blocks that target thread. In a loader, UI or hot hook
it can cause a deadlock or an apparent app hang. Avoid it for ordinary observation.

Use `frida.Cancellable` around operations with meaningful deadlines. Cancellation
is cooperative and does not kill the target. A controller timeout cannot guarantee
recovery from every native deadlock. Recent core releases also expose script
interrupt/terminate APIs; capability-check before using them and do not promise
that interrupting JavaScript unblocks an arbitrary synchronous native call.

Sources: [Python RPC example](https://github.com/frida/frida-python/blob/main/examples/rpc.py),
[generated Python bindings](https://frida.re/news/2026/07/17/frida-17-16-0-released/),
[script interruption](https://frida.re/news/2026/06/16/frida-17-14-0-released/).

## Cleanup and failures

Run teardown in `finally`. For attach, unload/detach and leave the user's process
alive. For an owned spawn that failed before resume, terminate it before detaching
so failure does not release uninstrumented execution. For a successfully resumed
spawn, choose and document whether it should continue or be killed on exit.
The runner defaults to continue and offers `--kill-on-exit` for its own spawn.

An optional `rpc.exports.dispose()` can restore manual patches and remove
observers before script unload. Interceptor cleanup alone cannot reverse a
manual `writeByteArray`/`Memory.patchCode` change or native side effects. Disposal
failure must be visible, especially if the attached application remains alive.

`sys.stdin.read()` is convenient in old interactive examples but is a poor
automation lifetime mechanism: redirected stdin may immediately hit EOF.
Use finite deadlines and an event set by target detachment. Record cleanup errors
without replacing the first causal error. Never use a process-name kill sweep.

## Child processes and gating

An instrumented parent does not imply instrumented children. For a controlled
multi-process experiment, adapt the upstream
[child-gating example](https://github.com/frida/frida-python/blob/main/examples/child_gating.py):

1. Register device child callbacks and maintain a PID → session/script registry.
2. Enable child gating on the parent session before its interesting action.
3. Queue each child for a worker that attaches, loads and waits for readiness.
4. Resume each accepted child exactly once after its hooks are ready.
5. Explicitly handle rejected/failed children; they must not remain suspended.
6. Disable gating, drain pending children and close all sessions on exit.

Device-wide spawn gating and per-session child gating have different scope and
backend support. Do not enable device-wide gating as a default Windows action;
it can affect unrelated process creation. A watchdog is not a replacement for
tracking and disposing every suspended PID. The bundled single-target runner
does not implement child gating; add it only when the case requires it.
