# GumJS notes for modern agents

This is a decision aid; check exact signatures in the installed runtime and the
[official JavaScript API](https://frida.re/docs/javascript-api/). The complete
imported API manual is a historical snapshot under `legacy/frida-docs/`.

## Migrating old examples to Frida 17

| Old form | Maintained form |
| --- | --- |
| `Module.getExportByName('x.dll', 'f')` | `Process.getModuleByName('x.dll').getExportByName('f')` |
| `Module.findExportByName(null, 'f')` | `Module.findGlobalExportByName('f')` (preserves null-on-miss semantics) |
| `Module.findBaseAddress('x.dll')` | `Process.findModuleByName('x.dll')?.base` |
| `Memory.readU32(p)` / `Memory.writeU32(p, n)` | `p.readU32()` / `p.writeU32(n)` |
| `Process.enumerateModulesSync()` / callback enumeration | `Process.enumerateModules()` returning an array |

Do not decide that an entire legacy API family is supported just because
`Process.getModuleByName` exists. That method predates Frida 17. Feature-test the
specific API when maintaining mixed versions, or keep one documented baseline.
Source: [Frida 17 migration](https://frida.re/news/2025/05/17/frida-17-0-0-released/).

## Memory, types, and lifetime

- Use `NativePointer`, `Int64`, `UInt64` for target values. JS numbers lose precision
  above 2^53−1. Serialize addresses as hex strings and 64-bit integers as strings.
- Null checks and mapped-range checks help, but mappings can race. Bound reads,
  catch memory errors, and respect the API's actual data length.
- `Memory.allocUtf16String`/`allocUtf8String` memory is tied to JS reachability.
  Store a replacement on the invocation's `this` for a synchronous call. If native
  code keeps the pointer, retain it beyond `onLeave` until the native owner releases
  it. Keeping every allocation forever is also a leak.
- `retval` objects may be recycled. Copy with `ptr(retval.toString())` if retaining
  a value beyond that callback.
- `ArrayBuffer.wrap` skips memory-access validation; prefer a copied `readByteArray`
  for analysis. `readVolatile` can help when memory is being freed concurrently,
  but is slower and not a reason to dump the entire address space.
- Use `Memory.scan` over selected readable ranges with known bounds, not arbitrary
  pointer arithmetic over unmapped gaps. Distinguish a unique match from a guess.

Source: [Frida memory/argument practices](https://frida.re/docs/best-practices/).

## Callback execution

```javascript
const hook = Interceptor.attach(address, {
  onEnter(args) { this.original = args[0]; },
  onLeave(retval) { send({result: retval.toString()}); }
});
// Later: hook.detach();
```

These callbacks execute on the intercepted thread. Avoid blocking, costly logs,
reentrant calls through the same API, and allocations in allocator hooks.
`NativeFunction` defaults and scheduling options affect reentrancy; use exclusive
scheduling only with a demonstrated need, since it can deadlock callbacks.

`Interceptor.replace` and `replaceFast` differ: the latter returns a pointer to
the original implementation that must be used for calls to it. Restore with
`Interceptor.revert`; flush when installing/changing hooks immediately before a
same-turn native call. Do not apply `detachAll` to remove another script's hooks
when you have handles for your own listeners.

## Tracing and cleanup

Choose `Stalker.follow(threadId, options)` for a selected native thread, optionally
`addCallProbe` or call summaries for a narrower question. Prefer summaries over
per-instruction JS callbacks. Exclude irrelevant modules, bound the time window,
unfollow, remove probes, and allow pending callbacks to drain before garbage
collection. Raw Stalker events need parsing; they are not printable strings.

Track all observers, timers, listeners and allocations in the agent's lifetime
model. An optional `rpc.exports.dispose()` provides a host-controlled cleanup
point. `Script.bindWeak` can release resources tied to objects; it is not a
substitute for explicit rollback of manual target memory changes.

Native `Process.setExceptionHandler` and JavaScript `try/catch` act at different
layers. A recovered Frida native call may still have left application state
partially modified. Preserve the failure and reproduce with less instrumentation.
