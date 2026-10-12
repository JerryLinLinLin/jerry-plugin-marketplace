# Windows hook design

## Resolve once, verify, and handle DLL lifetime

```javascript
const module = Process.getModuleByName('kernel32.dll');
const address = module.getExportByName('CreateFileW');
const owner = Process.findModuleByAddress(address);
send({type: 'resolved', requestedModule: module.name, address: address.toString(),
  owner: owner === null ? null : owner.name});
```

Exports may forward to KERNELBASE or an API-set implementation. Resolve what the
actual process exports instead of permanently hardcoding a DLL ownership table.
Deduplicate by resolved address if observing multiple facade DLLs. A wrapper and
its callee may still have different addresses: select one boundary when one event
per logical call is needed. Prefer
`findModuleByName`/`findExportByName` for optional modules; `get*` throws.
Global export lookup is convenient but expensive and potentially ambiguous.

For delayed loads, use `Process.attachModuleObserver({onAdded, onRemoved})`.
It includes modules already present and lets you instrument newly loaded modules.
Keep the callback short; avoid arbitrary native calls, heavy symbol loading,
blocking RPC and unconditional `Module.load()` in loader-sensitive execution.
Track listener ownership and discard stale addresses on unload so reloading at
the same address is not mistaken for an existing hook. Frida 17.16 fixed automatic
hook discard on module unload; old core versions need specific validation.
The bundled [windows-observe.js](../assets/windows-observe.js) shows a bounded
file-open observer and a ready handshake.

## RVA, VA and file offset

For an unexported function:

```text
RVA = disassembler virtual address - disassembler image base
runtime address = live module base + RVA
```

For a PE file offset within a file-backed section:

```text
RVA = file offset - section.PointerToRawData + section.VirtualAddress
```

Validate section bounds and the exact module build/hash; zero-filled virtual
data has no equivalent raw bytes. Verify code/instruction boundaries before
hooking. A stale RVA can be a valid address in unrelated code and still crash.
Pair with the available static tool (for example Rizin RE Toolkit) to establish
the RVA; this plugin does not require that other plugin to be installed.

Source: [Microsoft PE format](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format).

## Windows types and return conventions

| C type / convention | Frida handling |
| --- | --- |
| `BOOL`, `DWORD`, `ULONG`, `LONG`, `NTSTATUS`, `HRESULT` | 32-bit even on x64; use signed/unsigned conversion according to the declaration |
| `HANDLE`, pointer, `SOCKET`, `SIZE_T`, `ULONG_PTR` | Pointer-width; do not truncate with `toInt32()` to store identities |
| `LPCWSTR` / `LPWSTR` | Guarded UTF-16 read with a character bound |
| `LPCSTR` / `LPSTR` | Windows code-page string (`readAnsiString`), unless this API explicitly declares UTF-8 |
| `UNICODE_STRING` | Length is bytes; decode the pointed buffer with `Length / 2`; account for x86/x64 alignment |
| `BOOL` success | Nonzero, but outputs may still describe deferred work |
| `LSTATUS` / registry API | `ERROR_SUCCESS == 0`; do not apply BOOL logic |
| `NTSTATUS` / `HRESULT` | Signed success/failure conventions; use the specific API contract |
| `CreateFileW` failure | `INVALID_HANDLE_VALUE == ptr(-1)`, not null |

Specify a native call/callback signature from the actual prototype. x86 WINAPI
usually uses `stdcall`; C runtime exports usually use `mscdecl`. x64 Windows
uses `win64`/the platform default. C++ members, `thiscall`, varargs, structures
returned by value, vector arguments and ARM64 are separate cases. Do not force
`stdcall` on x64 or infer a signature from the number of apparent registers.

For a native Windows call where last error matters, `SystemFunction` returns
both `value` and `lastError`. In an Interceptor callback use `this.lastError`.
It is meaningful only when the API says so. If you change a return value, ensure
the output parameters and error state match it; fake success alone is often invalid.

Sources: [Windows data types](https://learn.microsoft.com/en-us/windows/win32/winprog/windows-data-types),
[x64 calling convention](https://learn.microsoft.com/en-us/cpp/build/x64-calling-convention?view=msvc-170).

## Bounded synchronous ReadFile capture

```javascript
const readFile = Process.getModuleByName('kernel32.dll').getExportByName('ReadFile');
let remaining = 50;
const listener = Interceptor.attach(readFile, {
  onEnter(args) {
    this.buffer = args[1];
    this.requested = args[2].toUInt32();
    this.completed = args[3];
    this.overlapped = args[4];
  },
  onLeave(retval) {
    if (remaining <= 0 || retval.toInt32() === 0 || !this.overlapped.isNull() || this.completed.isNull()) return;
    try {
      const actual = this.completed.readU32();
      const saved = Math.min(actual, this.requested, 4096);
      if (saved === 0) return;
      const bytes = this.buffer.readByteArray(saved);
      remaining--;
      send({type: 'read', tid: this.threadId, requested: this.requested,
        actual: actual, saved: saved, truncated: saved < actual}, bytes);
    } catch (error) {
      remaining--;
      send({type: 'read-error', description: error.message});
    }
  }
});
```

This intentionally captures only completed calls with `lpOverlapped == NULL`.
For overlapped I/O, correlate the original buffer, request, handle and OVERLAPPED
pointer with completion (`GetOverlappedResult`, completion port or callback).
`ERROR_IO_PENDING` is not final failure and the buffer is not ready at API return.
WSARecv/WSASend similarly require completion-aware WSABUF tracking. Handle reuse,
cancellation, thread changes and process exit all need cleanup of the correlation
map. A requested length is not an output length.

Source: [ReadFile contract](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-readfile).

## Common observation points

| Question | Candidate boundary | Interpretation trap |
| --- | --- | --- |
| File access | CreateFileW, ReadFile, WriteFile, CloseHandle | Path != file identity; relative paths, reparse points, async I/O, handle reuse |
| Registry use | RegOpenKeyExW, RegQueryValueExW, RegSetValueExW, RegCloseKey | Track parent handle + subkey and WOW64 view; return is LSTATUS |
| Network buffers | connect, send, recv, WSARecv, WinHTTP/WinINet | `recv > 0` gives length, 0 EOF, -1 failure; TLS may hide payload |
| Runtime crypto | BCryptDecrypt, CryptDecrypt, library-specific boundaries | Validate status and actual output size; streaming/padding/state matter |
| Cross-process behavior | OpenProcess, VirtualAllocEx, WriteProcessMemory, CreateRemoteThread | Remote addresses are not readable in the current process; a call is not proof of success |
| Dynamic code | VirtualAlloc/VirtualProtect, module observer, relevant internal decoder | Executable memory can be a JIT; protection change alone is not proof of malware |

Input buffers usually belong in `onEnter`; output in successful `onLeave` or the
actual async completion. Copy data while its lifetime is valid; storing only a
pointer for later Python RPC invites use-after-free. Keep per-call state on `this`
and use normal functions so Frida supplies the invocation context.

For frequent APIs, filter by caller module/thread/range before reading memory.
Batch `send` messages, avoid stack walking every call, and move genuinely hot
logic to a carefully checked CModule only when measurement justifies it.
