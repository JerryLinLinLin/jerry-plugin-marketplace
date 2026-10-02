# Frida JavaScript API cheatsheet (Windows native)

Terse grouped reference for native Windows security work. All signatures verbatim from the Frida 17-era `javascript-api.md`. `addr`/`ptr` = `NativePointer`. For Java/ObjC/Swift targets the bridges are lazy-loaded and out of scope here — see Frida's `Java`/`ObjC` docs. For host-side messaging (Python/Node), see `messages.md`.

## Globals

```js
ptr("0x7ffd0000")          // -> NativePointer; ptr(s) shorthand for new NativePointer(s)
NULL                       // == ptr("0")
int64(-1)  uint64("0xff")  // Int64 / UInt64 constructors (string "0x.." = hex)
hexdump(target, opts)      // target = NativePointer | ArrayBuffer; opts {address,offset,length,header:true,ansi:true}
console.log(line)          // also .warn/.error; ArrayBuffer args auto-hexdump'd
Script.runtime             // 'QJS' | 'V8'
Frida.version              // string;  Frida.heapSize -> number
```

Timers (Windows-agnostic, same as browser):

```js
const id = setTimeout(fn, ms, ...args); clearTimeout(id);
const iv = setInterval(fn, ms, ...args); clearInterval(iv);
setImmediate(fn, ...args); clearImmediate(id);
```

`send`/`recv`/`rpc.exports` are summarized at the end (full detail in `messages.md`).

## Process

```js
Process.id                       // PID (number)
Process.arch                     // 'ia32' | 'x64' | 'arm' | 'arm64'
Process.platform                 // 'windows' on Windows
Process.pointerSize              // 4 (x86) | 8 (x64)
Process.pageSize                 // usually 4096
Process.mainModule               // Module of the main .exe
Process.isDebuggerAttached()     // boolean
Process.getCurrentThreadId()     // OS thread id (number)
Process.enumerateThreads()       // [{id,name,state,context,...}]
Process.getModuleByName(name)    // Module; throws if not found
Process.findModuleByName(name)   // Module | null
Process.getModuleByAddress(addr) // Module containing addr; throws
Process.findModuleByAddress(addr)// Module | null
Process.enumerateModules()       // [Module]  (index 0 = main .exe)
Process.enumerateRanges('r-x')   // [{base,size,protection,file?}]; or {protection,coalesce:true}
Process.getRangeByAddress(addr)  // single range; findRangeByAddress -> null
Process.enumerateMallocRanges()  // heap allocations
Process.setExceptionHandler(cb)  // process-wide native exception hook (see below)
Process.runOnThread(id, cb)      // Promise; run JS on another thread (non-reentrant)
Process.getCurrentDir() / getHomeDir() / getTmpDir()
Process.codeSigningPolicy        // 'optional' | 'required'
```

`setExceptionHandler` detail — `details = {type, address, memory?:{operation,address}, context, nativeContext}`; `type` ∈ `abort, access-violation, guard-page, illegal-instruction, stack-overflow, arithmetic, breakpoint, single-step, system`. Return `true` to swallow and resume (you may fix up `context` regs first). Any `NativeFunction` whose faults you want delivered here must be built with `exceptions:'propagate'`.

## Module

Static helpers (Frida 17 — note `Module.*` export lookup is now per-instance, not `Module.getExportByName('dll','fn')`):

```js
Module.load('C:\\Windows\\System32\\win32u.dll')   // -> Module; throws on failure
Module.getGlobalExportByName('CreateFileW')         // addr across ALL modules; costly, avoid; throws
Module.findGlobalExportByName('CreateFileW')        // same, returns null on miss
```

Instance methods on a `Module` (`.name`, `.base`, `.size`, `.path`):

```js
const k32 = Process.getModuleByName('kernel32.dll');
k32.getExportByName('CreateFileW')      // absolute addr; throws if absent
k32.findExportByName('CreateFileW')     // addr | null
k32.getSymbolByName(name) / findSymbolByName(name)
k32.ensureInitialized()                 // run module initializers before use (early instrumentation)
k32.enumerateExports()                  // [{type:'function'|'variable', name, address}]
k32.enumerateImports()                  // [{type, name, module?, address?, slot?}]  (only name guaranteed)
k32.enumerateRanges('r-x')              // module-scoped ranges
k32.enumerateSections()                 // [{id,name,address,size}]
k32.enumerateDependencies()             // [{name, type:'regular'|'weak'|'reexport'|'upward'}]
```

Windows note: `Module.prototype.enumerateSymbols()` is NOT available on Windows — use `DebugSymbol` for symbolication. There is no `getBaseAddress`; use `.base`.

## ApiResolver

```js
const r = new ApiResolver('module');               // 'module' = exports/imports/sections; always available
r.enumerateMatches('exports:kernel32.dll!Create*') // [{name, address, size?}]
r.enumerateMatches('imports:*!*alloc*')
r.enumerateMatches('exports:*!Rtl*/i')             // trailing /i = case-insensitive
```

Reuse a resolver within a batch; recreate it to pick up newly loaded modules.

## Memory

```js
Memory.alloc(size[, {near, maxDistance}])   // page-granular if size is pageSize multiple; freed on GC — keep a ref
Memory.allocUtf16String('C:\\x')            // Windows "W" wide string buffer
Memory.allocAnsiString('hi')                // Windows ANSI buffer (Windows-only)
Memory.allocUtf8String('hi')
Memory.copy(dst, src, n)                    // memcpy
Memory.dup(addr, size)                      // alloc + copy -> NativePointer
Memory.protect(addr, size, 'rwx')           // -> bool; protections 'rwx','rw-','r-x',...
Memory.queryProtection(addr)                // -> protection string
Memory.patchCode(addr, size, code => {...}) // code is a WRITABLE ptr (may differ from addr); don't overflow
Memory.scan(addr, size, pattern, {onMatch(a,sz), onError(reason), onComplete()}) // async; onMatch may return 'stop'
Memory.scanSync(addr, size, pattern)        // -> [{address,size}]; throws on access error
```

Pattern syntax: `"13 37 ?? ff"` (`??` = any byte), r2 mask `"13 37 : 1f ff"`, nibble wildcards `"?3 37"`.

```js
// Scan only executable ranges of a module (avoids unreadable sections)
Process.getModuleByName('app.exe').enumerateRanges('r-x').forEach(({base,size}) => {
  Memory.scanSync(base, size, '48 8b ?? ?? ?? ?? ??').forEach(m => console.log(m.address));
});
```

## NativePointer (`ptr(s)`, `NULL`)

Arithmetic returns a new `NativePointer`; rhs may be number or `NativePointer`:

```js
p.add(rhs) p.sub(rhs) p.and(rhs) p.or(rhs) p.xor(rhs) p.shr(n) p.shl(n) p.not()
p.isNull()  p.equals(rhs)  p.compare(rhs)         // bool / bool / int
p.toInt32()  p.toString([radix=16])  p.toMatchPattern()   // toMatchPattern -> Memory.scan pattern for the raw value
```

Reads/writes:

```js
p.readPointer()              p.writePointer(ptr)
p.readU8/S8/U16/S16/U32/S32()  p.readFloat() p.readDouble()
p.writeU8(v) … p.writeDouble(v)
p.readU64() p.readS64() p.readLong() p.readULong()   p.writeU64(v) p.writeS64(v) …
p.readByteArray(len) -> ArrayBuffer   p.writeByteArray(buf | int[0..255])
p.readVolatile(len) / p.writeVolatile(bytes)         // exception-free (syscall-based); safe on possibly-unmapped memory
```

Strings (Windows — pick by API suffix: `…W` -> Utf16, `…A` -> Ansi):

```js
p.readUtf16String([length=-1])   // LPCWSTR / WCHAR* (UTF-16LE); -1 = NUL-terminated. length is in CHARACTERS
p.readAnsiString([size=-1])      // LPCSTR, code-page aware — Windows-ONLY
p.readUtf8String([size=-1])      // char* / UTF-8;  readCString for ASCII
p.writeUtf16String(str)          // encodes + NUL terminator (Windows wide)
p.writeAnsiString(str)           // Windows-only
p.writeUtf8String(str)
```

Reading a `…W` wide string with `readUtf8String()` yields only the first character — always use `readUtf16String()` for `W` APIs. PAC ops `p.sign()/strip()/blend()` are no-ops without pointer authentication.

## Int64 / UInt64 (`int64(v)`, `uint64(v)`)

```js
new Int64(v) / new UInt64(v)     // v = number or string ("0x.." hex)
x.add/sub/and/or/xor(rhs)  x.shr(n) x.shl(n)  x.compare(rhs)  x.toNumber()  x.toString([radix=10])
```

## Interceptor

```js
Interceptor.attach(target, callbacks[, data])   // target = NativePointer; returns listener with .detach()
Interceptor.replace(target, replacement[, data])// replacement = NativeCallback (or CModule ptr); kept alive until revert
Interceptor.replaceFast(target, replacement)    // lower overhead; RETURNS ptr to original — call it to reach original
Interceptor.revert(target)                       // restore original
Interceptor.detachAll()                          // detach all attach() hooks
Interceptor.flush()                              // commit pending hooks; needed before calling a just-hooked fn
```

Callbacks:
- `onEnter(args)` — `args` is an array of `NativePointer`, writable (`args[0] = ptr('...')`).
- `onLeave(retval)` — `retval` is `NativePointer`-derived; `retval.replace(1337)` or `retval.replace(ptr("0x1234"))`. **Recycled across calls — deep-copy with `ptr(retval.toString())` to keep.**

Per-invocation `this` (thread-local; stash state in `onEnter`, read in `onLeave`):

| `this.*` | meaning |
|---|---|
| `this.context` | `CpuContext` regs, assignable (see below) |
| `this.returnAddress` | `NativePointer` of caller |
| `this.lastError` | **Windows GetLastError** value, assignable (`this.errno` is the UNIX equivalent) |
| `this.threadId` | OS thread id |
| `this.depth` | call nesting depth |

`Interceptor.flush()` is auto-run on JS-runtime exit, `send()`, `console.*`, and RPC return — call it manually only when you hook then immediately invoke via `NativeFunction` in the same tick.

Windows `CreateFileW` example:

```js
const k32 = Process.getModuleByName('kernel32.dll');
const GENERIC_WRITE = 0x40000000;
Interceptor.attach(k32.getExportByName('CreateFileW'), {
  onEnter(args) {
    this.name   = args[0].readUtf16String();   // LPCWSTR
    this.access = args[1].toInt32();
  },
  onLeave(retval) {
    const mode = (this.access & GENERIC_WRITE) ? 'W' : 'R';
    // INVALID_HANDLE_VALUE == (HANDLE)-1; compare via equals(ptr(-1)) on x64, not toInt32()
    console.log(`[CreateFileW] ${mode} "${this.name}" -> ${retval}  GLE=${this.lastError}`);
  }
});
```

Replace + chain to original (set ABI on x86):

```js
const p = k32.getExportByName('CreateFileW');
const orig = new NativeFunction(p, 'pointer',
  ['pointer','uint','uint','pointer','uint','uint','pointer'],
  (Process.arch === 'ia32') ? { abi: 'stdcall' } : undefined);
Interceptor.replace(p, new NativeCallback(function (name, acc, sh, sa, disp, fl, tmpl) {
  const s = name.readUtf16String();
  if (s && s.toLowerCase().endsWith('secret.txt')) {
    this.lastError = 2;          // ERROR_FILE_NOT_FOUND, settable in the callback
    return ptr(-1);              // INVALID_HANDLE_VALUE
  }
  return orig(name, acc, sh, sa, disp, fl, tmpl);
}, 'pointer', ['pointer','uint','uint','pointer','uint','uint','pointer'],
   (Process.arch === 'ia32') ? 'stdcall' : undefined));
// later: Interceptor.revert(p);
```

## CpuContext

The object behind `this.context` (Interceptor), `Thread.context`, and the exception-handler `context`. All fields are `NativePointer` and assignable:

```js
ctx.pc   // instruction pointer (rip on x64, eip on x86)
ctx.sp   // stack pointer (rsp / esp)
// x64 GPRs: rax rbx rcx rdx rdi rsi rbp rsp rip r8 r9 r10 r11 r12 r13 r14 r15
// x86 GPRs: eax ebx ecx edx edi esi ebp esp eip
```

x64 WINAPI integer/pointer args 1–4 are in `RCX, RDX, R8, R9`, but `Interceptor` already abstracts these into `args[0..3]` — don't read registers by hand for argument access.

## NativeFunction / SystemFunction / NativeCallback

```js
new NativeFunction(address, retType, argTypes[, abi])
new NativeFunction(address, retType, argTypes[, options])  // {abi, scheduling, exceptions, traps}
new SystemFunction(address, retType, argTypes[, abi|options]) // returns {value, lastError} on Windows
new NativeCallback(func, retType, argTypes[, abi])           // JS-implemented native fn; also a NativePointer
```

- Variadic: place `'...'` in `argTypes` between fixed and variadic args.
- `options`: `scheduling:'cooperative'|'exclusive'`, `exceptions:'steal'|'propagate'` (use `'propagate'` to catch faults via `Process.setExceptionHandler`), `traps:'default'|'none'|'all'` (`'all'` re-enables Stalker during the call).
- `SystemFunction` snapshots thread last-error: `{value, lastError}` on Windows (`{value, errno}` on UNIX) — use when you need the original's `GetLastError`.
- Types: `void pointer int uint long ulong char uchar size_t ssize_t float double int8 uint8 int16 uint16 int32 uint32 int64 uint64 bool`.

ABIs (Windows):

| Arch | ABI strings |
|---|---|
| x86 (32-bit) | `'sysv'`, `'stdcall'`, `'thiscall'`, `'fastcall'`, `'mscdecl'` |
| x64 | `'win64'` (the default on x64) |

Most Win32 APIs are `__stdcall` on x86 — you MUST pass `abi:'stdcall'` for `NativeFunction`/`NativeCallback`/`replace` there. On x64 there is only `win64` (implicit). Reading args via `Interceptor.attach` needs no ABI.

Call a Win32 API yourself:

```js
const u = Process.getModuleByName('user32.dll');
const MessageBoxW = new NativeFunction(u.getExportByName('MessageBoxW'),
  'int', ['pointer','pointer','pointer','uint'],
  (Process.arch === 'ia32') ? { abi: 'stdcall' } : undefined);
const text = Memory.allocUtf16String('Hello');   // keep refs alive!
const cap  = Memory.allocUtf16String('Frida');
MessageBoxW(NULL, text, cap, 0x40 /* MB_ICONINFORMATION */);
```

```js
// SystemFunction to read the original's GetLastError
const VirtualProtect = new SystemFunction(
  Process.getModuleByName('kernel32.dll').getExportByName('VirtualProtect'),
  'int', ['pointer','size_t','uint','pointer']);
const r = VirtualProtect(addr, 0x1000, 0x40, scratch);
console.log(r.value, r.lastError);
```

## DebugSymbol + Thread.backtrace

`DebugSymbol` is the primary symbolication path on Windows (`Module.enumerateSymbols` is unavailable there):

```js
DebugSymbol.fromAddress(addr)   // {address,name,moduleName,fileName,lineNumber}; null fields if unknown; has toString()
DebugSymbol.fromName(name)
DebugSymbol.getFunctionByName(name)        // addr; throws
DebugSymbol.findFunctionsNamed(name)       // addr[]
DebugSymbol.findFunctionsMatching(glob)    // addr[]
DebugSymbol.load(path)                     // load symbols for a module
```

```js
Thread.backtrace([context, backtracer])    // [NativePointer]; max 16 frames
Thread.sleep(seconds)
// Backtracer.ACCURATE (default) or Backtracer.FUZZY (any binary, false positives)
Interceptor.attach(target, {
  onEnter() {
    console.log(Thread.backtrace(this.context, Backtracer.ACCURATE)
      .map(DebugSymbol.fromAddress).join('\n'));
  }
});
```

## MemoryAccessMonitor

```js
MemoryAccessMonitor.enable(ranges, { onAccess(details) {...} })  // ranges = {base,size} | [{base,size}]
MemoryAccessMonitor.disable()
```

Fires on the **first** access of each page. `details = {threadId, operation:'read'|'write'|'execute', from, address, rangeIndex, pageIndex, pagesCompleted, pagesTotal, context}` (context assignable). Useful for catching unpacking / self-modifying code writing then executing a region.

## File / Socket one-liners

```js
File.readAllBytes('C:\\x.bin')           // -> ArrayBuffer
File.readAllText('C:\\x.txt')            // -> string
File.writeAllBytes('C:\\o.bin', ab)
File.writeAllText('C:\\o.txt', 'hi')
const f = new File('C:\\log.bin', 'wb'); f.write(ab); f.flush(); f.close();
// f.readBytes([n]) f.readText([n]) f.readLine() f.seek(off[,File.SEEK_SET/CUR/END]) f.tell()
```

```js
Socket.connect({family:'ipv4', host:'127.0.0.1', port:8080})  // -> Promise<SocketConnection>
Socket.listen([{family,host,port}])                            // -> Promise<SocketListener>
Socket.type(handle)         // 'tcp'|'udp'|'tcp6'|... — inspect an OS socket handle from a hooked call
Socket.localAddress(handle) // {ip,port};  Socket.peerAddress(handle)
// SocketConnection is an IOStream: .input/.output (async), .setNoDelay(bool)
```

## send/recv & rpc.exports messaging

Agent-side primitives (host-side Python/Node handling lives in `messages.md`):

```js
send(message[, data]);      // message = JSON-serializable; data = ArrayBuffer | int[0..255] (e.g. from readByteArray)
                            // host receives {type:'send', payload:message}; batch — per-message overhead is high
recv([type,] callback);     // one-shot handler for next inbound msg (optionally filtered by .type); callback(message,data)

// Blocking receive — pause inside onEnter until host replies:
const op = recv('input', value => { /* apply value */ });
op.wait();                  // host sends via script.post({type:'input', payload:...})

rpc.exports = {
  dumpMem(addr, len) { return ptr(addr).readByteArray(len); },  // return value or Promise
  async scan(pat)    { /* ... */ }
};
// Host (Python): script.exports_sync.dump_mem(addr, len)  /  (Node): script.exports.dumpMem(...)
```

Uncaught JS errors surface to the host as `{type:'error', description, lineNumber, stack}`. See `messages.md` for the full host-side wiring.