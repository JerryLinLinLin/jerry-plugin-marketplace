# Reversible runtime patching

Choose the least invasive change that answers the user's question. Record the
original behavior, exact module build/RVA, original bytes or return contract,
intended effect and rollback before changing the running process. This guide
concerns in-memory changes; persistent PE editing is a separate task with section,
relocation, signature and file-integrity consequences.

## Arguments and return values

For a synchronous wide-string argument, allocate a replacement and retain it for
the call. Do not overwrite a literal or a buffer whose capacity is unknown:

```javascript
const address = Process.getModuleByName('user32.dll').getExportByName('MessageBoxW');
const listener = Interceptor.attach(address, {
  onEnter(args) {
    this.text = Memory.allocUtf16String('Controlled test message');
    args[1] = this.text;
  }
});
// Restore behavior: listener.detach();
```

If native code retains the pointer asynchronously, keep it alive until the native
owner is finished. Unloading the agent too early can then be unsafe.

An `onLeave` replacement changes the returned scalar; it does not undo side
effects or populate output buffers. Restrict the mutation by caller/input when
appropriate. Use the exact return convention: BOOL, HANDLE, HRESULT, NTSTATUS and
LSTATUS are not interchangeable. Keep `this.lastError`/output structures coherent
when testing an error path. Never infer a universal license/authentication or
certificate-validation patch from a toy boolean example.

## Replace a function

Use `Interceptor.replace(address, callback)` with a retained `NativeCallback` and
an exact prototype/ABI. Restore with `Interceptor.revert(address)`. Flush if a
same-turn `NativeFunction` call must see the change immediately.

Keep the original-call behavior explicit. Ordinary `replace` supports calls to
the original through a correctly constructed NativeFunction as documented;
`replaceFast` requires the original pointer it returns. Do not build an accidental
recursive call into the replacement. ABI mistakes may appear only on return or
on another thread, so validate the smallest controlled function first.

## Code bytes

Prefer Interceptor for whole-function behavior. When instruction bytes really
must change:

1. Verify module identity and compute the RVA for this exact build.
2. Compare **all** expected bytes before writing. Decode enough whole instructions;
   a short byte count is not proof that the replacement ends at a boundary.
3. Ensure the patch is not racing threads executing that region. A Frida callback
   does not imply a process-wide stop; multi-byte writes are not generally atomic.
4. Use `Memory.patchCode(address, size, writable => ...)` for code. The writable
   pointer may differ from the execution address; writers need the original PC
   for relative instructions. Do not assume permanent RWX permissions are needed.
5. Read back and verify bytes and behavior. Restore the original bytes explicitly,
   verify again, and preserve a record of both observations.

This helper compares bytes on both apply and rollback. The caller must first
establish the build, instruction boundaries and quiescent execution preconditions:

```javascript
function patchBytes(target, expected, replacement) {
  if (expected.length === 0 || expected.length !== replacement.length) {
    throw new Error('Expected and replacement lengths must match');
  }
  const matches = bytes => {
    const actual = new Uint8Array(target.readByteArray(bytes.length));
    return bytes.every((value, index) => value === actual[index]);
  };
  if (!matches(expected)) throw new Error('Original bytes do not match');
  Memory.patchCode(target, replacement.length, writable => writable.writeByteArray(replacement));
  if (!matches(replacement)) throw new Error('Patch verification failed');
  return function restore() {
    if (!matches(replacement)) throw new Error('Region changed; refusing blind rollback');
    Memory.patchCode(target, expected.length, writable => writable.writeByteArray(expected));
    if (!matches(expected)) throw new Error('Rollback verification failed');
  };
}
```

Keep immutable copies of the byte arrays and do not call restore after the mapping
has unloaded/reloaded. For an instruction writer, select the correct architecture,
pass the execution PC for relative instructions, and call its `flush()`.
The helper does not supply a universal patch or default NOP count. Raw replacement
bytes must be known for the chosen function, not guessed from a tutorial.
Do not edit a module region already owned by an active Interceptor or another
debugger without resolving that conflict. An unload/reload invalidates saved
addresses even if a new module reuses the same base.

## Cleanup and interpreting a result

Interceptor listener detach and replacement revert are separate from manual
memory restoration. Provide `rpc.exports.dispose()` when an agent makes manual
changes so the controller can request rollback before unload. A script crash or
forced target kill may prevent that cleanup; keep the original data in the case
artifacts and do not claim rollback happened without observing it.

Validate both directions: original behavior → modified behavior → restored
behavior. Test the same input, then relevant alternate paths. A runtime patch
working once is evidence for that build and exercised path, not proof of thread
safety or a durable on-disk patch.

Sources: [Interceptor / Memory.patchCode API](https://frida.re/docs/javascript-api/),
[allocation lifetime](https://frida.re/docs/best-practices/),
[Windows API prototype examples](https://fuzzysecurity.com/tutorials/29.html).
