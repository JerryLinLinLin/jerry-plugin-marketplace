# Anti-debug & anti-Frida bypass with Frida (Windows)

Malware, packers, and protected commercial software actively look for debuggers and for Frida itself, then alter behavior (bail out, crash, fake results) when they find one. This cookbook neutralizes each check **from a Frida script** on Windows. Every technique gives a 1-2 line "How it detects" and a copy-paste Frida bypass snippet. Categories and exact "clean" values are mapped to the bundled Anti-Debug-DB encyclopedia files under `references/anti-debug-db/` (`debug-flags.md`, `timing.md`, `exceptions.md`, `process-memory.md`, `object-handles.md`, `assembly.md`, `interactive.md`, `misc.md`).

## Ground rules (read first)

```
frida -f target.exe -l bypass.js              # spawn SUSPENDED, hooks land before first instruction
frida -f target.exe -l bypass.js -o log.txt   # tee console.log to a file (no console artifacts in target)
frida -p <pid>     -l bypass.js               # attach to a running process
```

- **Always `-f` (spawn) for anti-debug.** Most checks fire early in `DllMain`/CRT init; attaching late misses them. `-f` holds the main thread suspended so your `onEnter` hooks are installed first.
- **Two ways to neutralize a function** — pick based on whether the function body is integrity-checked (see B6):
  - `Interceptor.attach(p, { onLeave(r){ r.replace(0); } })` — original body still runs; fix the result/out-buffer.
  - `Interceptor.replace(p, new NativeCallback(...))` — original body never runs; required when you must *swallow* a syscall.
- **Direct-memory checks cannot be hooked.** Many checks read the PEB (`gs:[60h]`) or execute `rdtsc`/`cpuid` with **no API call**. You must patch the data (PEB fields) or use Stalker — `Interceptor` only catches calls. See A4 and A9.
- **Frida 17 export resolution** is used throughout: `Process.getModuleByName('dll').getExportByName('fn')` throws on miss; `.findExportByName('fn')` returns `null`. (Pre-17 scripts use the two-arg `Module.getExportByName('dll','fn')`, which was **removed in Frida 17**.) If you need one script to run on both, paste this shim once and call `resolve('dll','fn')`:
  ```javascript
  function resolve(mod, exp) {
    return (typeof Process.getModuleByName === 'function')
      ? Process.getModuleByName(mod).getExportByName(exp)   // Frida 17+
      : Module.getExportByName(mod, exp);                   // legacy ≤16
  }
  ```
- All struct offsets below are **x64** unless a line is marked x86. Pointers are 8 bytes (`readPointer`/`writePointer`); flag fields are bytes/DWORDs.

---

# PART A — Anti-debug bypass

## A1. IsDebuggerPresent — `debug-flags.md` §1.1

**How it detects:** `kernel32!IsDebuggerPresent` just returns `PEB.BeingDebugged` (byte at `PEB+0x02`). Non-zero ⇒ debugged.

```javascript
Interceptor.attach(Process.getModuleByName('kernel32.dll').getExportByName('IsDebuggerPresent'), {
  onLeave(retval) { retval.replace(0); }
});
```

> Because this API only reads the PEB, **patching `BeingDebugged` (A4) defeats both the API and any inline `gs:[60h]+2` read at once.** Prefer A4 if the target also reads the PEB directly.

## A2. CheckRemoteDebuggerPresent — `debug-flags.md` §1.2

**How it detects:** writes `TRUE` into out-param `pbDebuggerPresent` (`args[1]`). Internally calls `NtQueryInformationProcess(ProcessDebugPort)`.

```javascript
Interceptor.attach(Process.getModuleByName('kernel32.dll').getExportByName('CheckRemoteDebuggerPresent'), {
  onEnter(args) { this.pOut = args[1]; },          // PBOOL
  onLeave() { if (!this.pOut.isNull()) this.pOut.writeU32(0); }
});
```

> Canonical fix per the DB is hooking `NtQueryInformationProcess` (A3), since this funnels through it.

## A3. NtQueryInformationProcess — `debug-flags.md` §1.3

**How it detects:** `NtQueryInformationProcess(h, InfoClass, pInfo, len, pRetLen)` with debug-related classes. "Clean" values:

| InfoClass | Value | Debugged means | Write on success |
|---|---|---|---|
| `ProcessDebugPort` | `0x07` | `*pInfo == -1` | `0` (pointer-sized) |
| `ProcessDebugFlags` | `0x1F` | `*pInfo == 0` | non-zero, e.g. `1` (DWORD) |
| `ProcessDebugObjectHandle` | `0x1E` | handle non-null | `0` (pointer-sized) |

One hook fixes all three in `onLeave`:

```javascript
const ProcessDebugPort = 0x07, ProcessDebugFlags = 0x1F, ProcessDebugObjectHandle = 0x1E;
Interceptor.attach(Process.getModuleByName('ntdll.dll').getExportByName('NtQueryInformationProcess'), {
  onEnter(args) { this.cls = args[1].toInt32(); this.pInfo = args[2]; },
  onLeave(retval) {
    if (retval.toInt32() < 0 || this.pInfo.isNull()) return;   // only fix NT_SUCCESS
    switch (this.cls) {
      case ProcessDebugPort:         this.pInfo.writePointer(ptr(0)); break;  // not -1
      case ProcessDebugFlags:        this.pInfo.writeU32(1);          break;  // non-zero = no dbg
      case ProcessDebugObjectHandle: this.pInfo.writePointer(ptr(0)); break;  // null handle
    }
  }
});
```

> Gotcha: `ProcessDebugPort`/`ProcessDebugObjectHandle` out-values are **pointer-sized** on x64 (`writePointer`); `ProcessDebugFlags` is a **DWORD** (`writeU32`). Add `ProcessBasicInformation (0x00)` here too if the binary does parent-PID checks (see A14 / B7).

## A4. PEB direct reads: BeingDebugged + NtGlobalFlag + heap flags — `debug-flags.md` §2.1/2.2/2.3/2.4

**How it detects (no API — raw memory reads):**
- `BeingDebugged`: byte at `PEB+0x02`.
- `NtGlobalFlag`: DWORD at `PEB+0xBC` (x64) / `PEB+0x68` (x86). Debugger-spawned ⇒ bits `0x70` set (`FLG_HEAP_ENABLE_TAIL_CHECK | FREE_CHECK | VALIDATE_PARAMETERS`).
- Process-heap `Flags`/`ForceFlags`: non-`HEAP_GROWABLE` / non-zero ⇒ debugged. The `0xABABABAB`/`0xFEEEFEEE` heap-tail sentinels (§2.4) are a *consequence* of the NtGlobalFlag heap bits.

**Bypass — patch the PEB once at startup** (cannot be hooked; you overwrite the memory the check reads):

```javascript
// RtlGetCurrentPeb() is exported by ntdll and returns the PEB pointer directly.
// (NtCurrentTeb is a compiler intrinsic reading gs:[0x30], NOT an ntdll export.)
const RtlGetCurrentPeb = new NativeFunction(Process.getModuleByName('ntdll.dll').getExportByName('RtlGetCurrentPeb'), 'pointer', []);
const peb = RtlGetCurrentPeb();

peb.add(0x02).writeU8(0);      // BeingDebugged = FALSE
peb.add(0xBC).writeU32(0);     // NtGlobalFlag = 0          (x86: PEB+0x68)

// Process heap: pointer at PEB+0x30 (x64). Vista+ Flags @0x70, ForceFlags @0x74.
const heap = peb.add(0x30).readPointer();
heap.add(0x70).writeU32(2);    // HEAP_GROWABLE
heap.add(0x74).writeU32(0);    // ForceFlags = 0
```

> **x86 differences:** PEB via `fs:[30h]`; `NtGlobalFlag` at `+0x68`; heap base at PEB `+0x18` (or `+0x1030` under WOW64); Vista+ heap `Flags`/`ForceFlags` at `+0x40`/`+0x44`.
>
> Zeroing `NtGlobalFlag` **before** the target allocates is the clean fix for the §2.4 heap-tail sentinel check. If allocations already happened, hook `HeapAlloc` and scrub the trailing sentinel bytes instead.

## A5. NtQuerySystemInformation(SystemKernelDebuggerInformation) + Rtl heap queries — `debug-flags.md` §1.4/1.5/1.6

**How it detects:** class `SystemKernelDebuggerInformation = 0x23` returns `SYSTEM_KERNEL_DEBUGGER_INFORMATION{ BOOLEAN DebuggerEnabled; BOOLEAN DebuggerNotPresent; }`. Kernel debugger present if `DebuggerEnabled && !DebuggerNotPresent`.

```javascript
const SystemKernelDebuggerInformation = 0x23;
Interceptor.attach(Process.getModuleByName('ntdll.dll').getExportByName('NtQuerySystemInformation'), {
  onEnter(args) { this.cls = args[0].toInt32(); this.pInfo = args[1]; },
  onLeave(retval) {
    if (retval.toInt32() < 0 || this.pInfo.isNull()) return;
    if (this.cls === SystemKernelDebuggerInformation) {
      this.pInfo.writeU8(0);          // DebuggerEnabled = FALSE
      this.pInfo.add(1).writeU8(1);   // DebuggerNotPresent = TRUE
    }
  }
});
```

> For the `RtlQueryProcessHeapInformation` / `RtlQueryProcessDebugInformation` variants (§1.5/1.6), hook the same way and force `Heaps[0].Flags = HEAP_GROWABLE` in the returned debug buffer.

## A6. NtSetInformationThread(ThreadHideFromDebugger=0x11) — `interactive.md` §4

**How it detects/attacks:** not a boolean — it *blinds* the debugger by detaching the thread from debug events (and can crash a debugger holding a BP in that thread).

**Bypass — you must `replace` (not `attach`) to swallow the syscall** when class == `0x11`:

```javascript
const ThreadHideFromDebugger = 0x11;
const NtSet = Process.getModuleByName('ntdll.dll').getExportByName('NtSetInformationThread');
const origNtSet = new NativeFunction(NtSet, 'int', ['pointer', 'int', 'pointer', 'int']);
Interceptor.replace(NtSet, new NativeCallback((h, cls, info, len) => {
  if (cls === ThreadHideFromDebugger) return 0;     // STATUS_SUCCESS, no-op
  return origNtSet(h, cls, info, len);
}, 'int', ['pointer', 'int', 'pointer', 'int']));
```

> `attach` cannot stop the call from reaching the kernel — only `replace` can. Hook this **early**; once a thread is hidden, a late attach may already have lost it.

## A7. CloseHandle / NtClose invalid-handle exception — `object-handles.md` §3

**How it detects:** closing a bogus handle (e.g. `0xDEADBEEF`) raises `EXCEPTION_INVALID_HANDLE (0xC0000008)` **only under a debugger**; a `__try/__except` that catches it ⇒ debugged.

```javascript
const ntdll = Process.getModuleByName('ntdll.dll');
const NtClose = ntdll.getExportByName('NtClose');
const origClose = new NativeFunction(NtClose, 'int', ['pointer']);
const NtQueryObject = new NativeFunction(
  ntdll.getExportByName('NtQueryObject'),
  'int', ['pointer', 'int', 'pointer', 'int', 'pointer']);

Interceptor.replace(NtClose, new NativeCallback((h) => {
  // Probe validity (ObjectBasicInformation=0). If invalid, return the failure
  // status WITHOUT raising the debug exception the kernel raises under a debugger.
  const st = NtQueryObject(h, 0, NULL, 0, NULL);
  if (st === (0xC0000008 | 0)) return 0xC0000008 | 0;   // STATUS_INVALID_HANDLE, no exception
  return origClose(h);
}, 'int', ['pointer']));
```

> Alternative: hook `kernel32!CloseHandle` `onEnter`, and if `args[0]` isn't a plausible handle, skip the call and force a benign return. The robust general route for exception-driven checks is A12.

## A8. OutputDebugString / DbgPrint / DbgSetDebugFilterState — `interactive.md` §7, `misc.md` §4/§5

**How it detects:**
- Legacy `OutputDebugStringA/W`: error state changes only when **no** debugger is present (deprecated).
- `RaiseException(DBG_PRINTEXCEPTION_C = 0x40010006)` / `DbgPrint`: the exception is consumed by a debugger; if the app's own handler catches it ⇒ no debugger (`misc.md` §4).
- `NtSetDebugFilterState` / `DbgSetDebugFilterState` succeeds under a kernel/some user-mode debuggers (`misc.md` §5).

```javascript
// OutputDebugString -> no-op (leaves LastError untouched)
const k32 = Process.getModuleByName('kernel32.dll');
for (const n of ['OutputDebugStringA', 'OutputDebugStringW']) {
  const a = k32.findExportByName(n);
  if (a) Interceptor.replace(a, new NativeCallback(() => {}, 'void', ['pointer']));
}
// DbgSetDebugFilterState -> force failure (per misc.md mitigation)
const dsf = Process.getModuleByName('ntdll.dll').findExportByName('NtSetDebugFilterState');
if (dsf) Interceptor.replace(dsf, new NativeCallback(() => 0xC0000001 | 0, 'int', ['int', 'int', 'int']));
```

> For `RaiseException`-based `DBG_PRINTEXCEPTION_C` / `DBG_CONTROL_C` checks, the clean route is to let the app's `__except` actually see the exception — see A12.

## A9. Timing: RDTSC / GetTickCount / QueryPerformanceCounter / timeGetTime — `timing.md` §1–7

**How it detects:** measure elapsed ticks across a region; a large delta ⇒ single-stepping/breakpoints. `timing.md` mitigation: "hook timing functions and accelerate the time between calls" — make the app see tiny deltas.

API-based clocks are easy to hook:

```javascript
const k32 = Process.getModuleByName('kernel32.dll');
// GetTickCount / GetTickCount64 -> slowly advancing fake clock
let fakeTick = 0;
const gtc = k32.findExportByName('GetTickCount');
if (gtc) Interceptor.replace(gtc, new NativeCallback(() => (fakeTick += 1) >>> 0, 'uint32', []));

// QueryPerformanceCounter -> monotonic, tiny increments in the out-param
let qpc = 0;
Interceptor.attach(k32.getExportByName('QueryPerformanceCounter'), {
  onEnter(args) { this.p = args[0]; },        // LARGE_INTEGER*
  onLeave() { qpc += 1000; this.p.writeU64(uint64(qpc)); }
});
```

**RDTSC (`0F 31`) is NOT a function call** — `Interceptor.attach` cannot catch it. Options, easiest first:
1. **Patch the comparison, not the read.** Find the timing check's branch and NOP/flip it with `Memory.patchCode` (see A11). `timing.md` notes timing checks are "not very reliable," so one API hook above is often enough.
2. **Stalker transform**: instrument the thread and rewrite `rdtsc` instructions to call a JS callback returning a controlled counter (higher effort).
3. Static `Memory.scan` for `0F 31` and overwrite with a `call` into a `NativeCallback` (alignment-sensitive; brittle).

## A10. Hardware breakpoints via GetThreadContext (Dr0–Dr7) — `process-memory.md` §1.4

**How it detects:** `GetThreadContext(CONTEXT_DEBUG_REGISTERS)`; non-zero `Dr0..Dr3` ⇒ a hardware breakpoint is set.

```javascript
// x64 CONTEXT debug regs: Dr0 @0x48, Dr1 @0x50, Dr2 @0x58, Dr3 @0x60, Dr6 @0x68, Dr7 @0x70
Interceptor.attach(Process.getModuleByName('kernel32.dll').getExportByName('GetThreadContext'), {
  onEnter(args) { this.ctx = args[1]; },
  onLeave(retval) {
    if (retval.toInt32() === 0 || this.ctx.isNull()) return;
    for (const off of [0x48, 0x50, 0x58, 0x60, 0x68, 0x70]) this.ctx.add(off).writeU64(0);
  }
});
```

> **x86 CONTEXT:** Dr0..Dr3 at `+0x04..+0x10`, Dr6 `+0x14`, Dr7 `+0x18`. Verify via `ContextFlags`. Also hook **`Wow64GetThreadContext`** for 32-bit-on-64 targets. Under pure Frida you usually have no HW breakpoints set anyway — this matters when layering Frida on a real debugger.

## A11. Software breakpoints / code checksums / inline-hook detection — `process-memory.md` §1.1, §2.2, §2.5; `assembly.md`

**How it detects:** scan own code for `0xCC` (INT3); CRC32 a function and compare; compare the first bytes of e.g. `IsDebuggerPresent` against the on-disk or a fresh in-memory copy to spot patches. **This same class catches Frida's own Interceptor trampolines** (see B6).

**Key consequence:** prefer `onLeave` retval tweaks and PEB patching (A1/A3/A4) over `Interceptor.replace`/`attach` of the *checked* function — those write a trampoline into the prologue that a first-bytes/checksum comparison detects. When you must defeat the check, **patch the check site, not the API**:

```javascript
// Force a 'jz being_debugged' branch never to take it: overwrite with NOPs (or a jmp).
const base = Process.mainModule.base;       // main EXE base
const site = base.add(0x1234);              // offset of the conditional branch
Memory.patchCode(site, 2, code => code.writeByteArray([0x90, 0x90]));   // NOP NOP
```

> `process-memory.md` notes for software-BP / anti-step-over there is "no possibility of interfering… they access memory directly" — so the practical answer is to leave **no hookable artifact** in the scanned function and patch the consumer/branch instead.

## A12. Exception-based checks (INT3, INT 2D, ICE 0xF1, single-step/trap flag, prefixes, guard-page) — `assembly.md`, `exceptions.md`, `process-memory.md` §1.3

**How it detects:** deliberately trigger an exception; under a debugger the debugger swallows it (the app's handler isn't reached) ⇒ inferred debugged. These hinge on **who receives the exception**.

**Important:** with **Frida alone (no debugger attached)** Frida does not sit in the SEH/VEH chain like a debugger, so most of A12 already returns the benign "not debugged" branch for free. You only fight these when Frida is layered on x64dbg/WinDbg. When you must intervene:

```javascript
// Process-wide native exception hook — recover as if no debugger swallowed it.
Process.setExceptionHandler(function (details) {
  // details.type ∈ {breakpoint, single-step, access-violation, guard-page, illegal-instruction, ...}
  if (details.type === 'breakpoint' || details.type === 'single-step') {
    // Step the instruction pointer past the faulting opcode and continue as "handled".
    details.context.pc = details.context.pc.add(1);   // size depends on the opcode (int3=1)
    return true;                                       // swallow & resume
  }
  return false;                                        // let the app's own handler run
});
```

Other levers:
- `SetUnhandledExceptionFilter` (`exceptions.md` §1): ensure the app's filter is actually installed/called (usually already true under Frida-only).
- `AddVectoredExceptionHandler`: install your own VEH from native via `NativeFunction`, fix up `CONTEXT.Rip/Eip`, return `EXCEPTION_CONTINUE_EXECUTION`.
- Guard-page / memory-BP (`process-memory.md` §1.3): driven by `PAGE_GUARD`; if needed, hook `VirtualProtect`/`VirtualAlloc` and strip `PAGE_GUARD (0x100)`.

> Most situational category. `assembly.md`/`exceptions.md` stress these are debugger-specific. Treat as no-ops under Frida-only and revisit only when combined with a real debugger.

## A13. Object-handle tricks: OpenProcess(csrss) / exclusive CreateFile / NtQueryObject(DebugObject) — `object-handles.md`

**How it detects:** open `csrss.exe` (succeeds only with debug privilege); exclusive `CreateFile` on own image fails if a debugger holds a handle; `NtQueryObject(ObjectAllTypesInformation = 3)` counts system-wide `DebugObject` instances.

```javascript
// NtQueryObject: zero DebugObject counts in the returned type table.
Interceptor.attach(Process.getModuleByName('ntdll.dll').getExportByName('NtQueryObject'), {
  onEnter(args) { this.cls = args[1].toInt32(); this.pInfo = args[2]; },
  onLeave(retval) {
    if (this.cls !== 3 || retval.toInt32() < 0 || this.pInfo.isNull()) return;
    // Walk OBJECT_ALL_INFORMATION; when a TypeName UNICODE_STRING == "DebugObject",
    // zero its TotalNumberOfObjects / TotalNumberOfHandles fields.
  }
});

// OpenProcess on csrss -> return NULL if the target is csrss (look up its PID first).
// Exclusive CreateFile / LoadLibrary checks: "too generic / no mitigation" per the file —
// patch the check site instead (Memory.patchCode NOP, see A11).
```

## A14. Interactive / misc: self-debug, BlockInput, FindWindow, parent-process, NtYieldExecution — `interactive.md`, `misc.md`

**How they detect:** spawn a child to `DebugActiveProcess(parent)` (self-debug lock); `BlockInput`/`SuspendThread`/`SwitchDesktop` to thwart a human analyst; `FindWindow` for known debugger window classes; parent process != `explorer.exe`; `NtYieldExecution`/`SwitchToThread` timing.

```javascript
const k32 = Process.getModuleByName('kernel32.dll');
const user32 = Process.getModuleByName('user32.dll');
const ntdll = Process.getModuleByName('ntdll.dll');

// Self-debugging (interactive.md §1): pretend the child's attach succeeded.
{ const a = k32.findExportByName('DebugActiveProcess');
  if (a) Interceptor.attach(a, { onLeave(r) { r.replace(1); } }); }

// FindWindow* (misc.md §1): hide debugger windows -> "not found".
['FindWindowA', 'FindWindowW', 'FindWindowExA', 'FindWindowExW'].forEach(n => {
  const a = user32.findExportByName(n);
  if (a) Interceptor.attach(a, { onLeave(r) { r.replace(0); } });
});

// BlockInput (interactive.md §3): neuter.
{ const a = user32.findExportByName('BlockInput');
  if (a) Interceptor.replace(a, new NativeCallback(() => 1, 'int', ['int'])); }

// NtYieldExecution / SwitchToThread (misc.md §6): force STATUS_NO_YIELD_PERFORMED.
{ const a = ntdll.findExportByName('NtYieldExecution');
  if (a) Interceptor.replace(a, new NativeCallback(() => 0x40000024 | 0, 'int', [])); }
```

> **Parent-process check (misc.md §2):** hook `NtQueryInformationProcess(ProcessBasicInformation = 0)` and overwrite `InheritedFromUniqueProcessId` with explorer.exe's PID; and/or filter `NtQuerySystemInformation(SystemProcessInformation)` to rename `python`/`frida` parents. `GetWriteWatch` (misc.md §7): hook `VirtualAlloc` to spot `MEM_WRITE_WATCH (0x00200000)` and return the expected hit count.

---

# PART B — Anti-Frida bypass

Frida itself leaves artifacts: a named agent DLL, distinctively named helper threads, a default TCP port, literal strings in agent memory, and inline trampolines. Each is detectable and each has a mitigation. Primary refs: crackinglandia "I know you're there, Frida!", `TheQmaks/phantom-frida` (16-vector renaming build), Layakk, romainthomas.

## B1. Loaded-module scan for the agent DLL

**How it detects:** enumerate modules and match `frida-agent-32.dll` / `frida-agent-64.dll` (also `frida-gadget`), or check for the export `frida_agent_main`. The app walks modules via `CreateToolhelp32Snapshot`+`Module32NextW` or the PEB `Ldr` `InLoadOrderModuleList`.

**Counter — filter the enumeration, or rename the artifact:**

```javascript
// Hide frida modules from Toolhelp walkers.
const k32 = Process.getModuleByName('kernel32.dll');
['Module32FirstW', 'Module32NextW'].forEach(n => {
  const a = k32.findExportByName(n);
  if (!a) return;
  Interceptor.attach(a, {
    onEnter(args) { this.me = args[1]; },           // LPMODULEENTRY32W
    onLeave(retval) {
      if (retval.toInt32() === 0 || this.me.isNull()) return;
      // MODULEENTRY32W.szModule is at offset 0x30 (wide). Skip frida entries by
      // advancing to the next module so the caller never sees this one.
      const name = this.me.add(0x30).readUtf16String() || '';
      if (/frida|gum|gadget/i.test(name)) { /* re-call Next / blank the name */ }
    }
  });
});
```

- **Rename the agent** (phantom-frida vectors #2/#5): renamed/patched build with `libfrida-agent` and `frida_agent_main` renamed — defeats both name and export scans at the binary level.
- Prefer **frida-gadget** embedded under a benign filename, or a custom loader. PEB-walkers also require scrubbing the `Ldr` `InLoadOrderModuleList`.

## B2. Thread-name scan: gum-js-loop / gmain / gdbus / pool-frida / pool-spawner

**How it detects:** Frida's helper threads carry these names. On Windows they're readable via `GetThreadDescription` / `NtQueryInformationThread(ThreadNameInformation = 38)` or via `Thread32First/Next` tooling.

**Counter:**

```javascript
// Blank Frida thread names returned by GetThreadDescription.
const gtd = Process.getModuleByName('kernel32.dll').findExportByName('GetThreadDescription');
if (gtd) Interceptor.attach(gtd, {
  onEnter(args) { this.ppwsz = args[1]; },          // PWSTR* (out)
  onLeave(retval) {
    if (retval.toInt32() < 0 || this.ppwsz.isNull()) return;
    const s = this.ppwsz.readPointer();
    if (!s.isNull()) {
      const name = s.readUtf16String() || '';
      if (/gum-js-loop|gmain|gdbus|pool-frida|pool-spawner/i.test(name)) s.writeUtf16String('');
    }
  }
});
```

- Also hook `NtQueryInformationThread(ThreadNameInformation)` to blank the returned `UNICODE_STRING`, and the app's `Thread32First/Next` walk to drop gum/gmain/gdbus threads.
- **Renamed Frida build** (phantom-frida) renames `gum-js-loop`/`gmain`/`gdbus`/`pool-spawner` at the source — the durable fix.

## B3. Default TCP port 27042 (and 27043)

**How it detects:** `connect()` to `127.0.0.1:27042`; if open, frida-server is listening. Advanced scanners sweep ports and send the Frida/D-Bus handshake.

**Counter — don't expose the port:**

```powershell
# Run frida-server on a non-default port, then point tools at it with -H.
frida-server.exe -l 127.0.0.1:8088
frida-ps -H 127.0.0.1:8088
frida   -H 127.0.0.1:8088 -n target.exe -l bypass.js
```

- Or **spawn-inject / embedded gadget** — local `frida -f target.exe` and a script-mode gadget open **no TCP port at all**.
- Custom port defeats fixed-port checks but **not** handshake sweeps. For those you need a patched server whose handshake strings differ (phantom-frida vectors #8/#10/#11 rename the D-Bus/protocol strings).

## B4. Named-pipe / agent IPC artifacts

**How it detects:** the injected agent talks to the host over a **named pipe**; the app enumerates process handles, filters type == pipe, and inspects the pipe name for `frida`.

**Counter:** hook the handle-enumeration path — `NtQuerySystemInformation(SystemHandleInformation)` and `NtQueryObject` name queries — and filter Frida pipe handles; or use a renamed build so the pipe/socket name contains no `frida`.

## B5. Memory string scan: FridaScriptEngine / frida:rpc / GumScript / re.frida.server

**How it detects:** sweep RW/RX regions for literal Frida strings baked into the agent: `FridaScriptEngine`, `frida_agent_main`, `frida:rpc`, `GumScript`/`GumJS`, GType `FridaServer`, the D-Bus `AUTH` handshake / `re.frida.server`.

**Counter:**
- **Patched/renamed Frida build** that also sweeps residual strings (phantom-frida vector #14, "binary string sweep") — the durable fix.
- At runtime you *can* hook the app's scanner (`RtlCompareMemory`/`memcmp` or its `VirtualQuery` walk) but it's brittle; a clean agent binary is preferred.
- D-Bus auth string: phantom-frida renames the service/interfaces (#8/#10) so handshake bytes don't match known signatures.

## B6. Interceptor trampoline / inline-hook integrity checks

**How it detects:** `Interceptor.attach`/`replace` writes a `jmp` (`E9 …`, or a longer 64-bit absolute jump) over the function prologue (e.g. over Windows' `mov edi,edi` hotpatch stub). Defenders then:
- read the first bytes and compare to the on-disk copy (`process-memory.md` §2.2),
- CRC32 the function (`process-memory.md` §2.5),
- compare the in-memory prologue against a fresh copy in another process (ASLR keeps system DLLs at the same base per boot).

> **There is no hardware-breakpoint hooking on Frida's standard Windows backend.** `Interceptor.attach`/`replace` *always* write an inline trampoline into the target function, so a first-byte/checksum scan of that function *will* see it. (`Interceptor.breakpointKind = 'hard'` exists only in Frida's **Barebone** bare-metal backend — it has no effect here.) The answer is therefore to leave **no artifact in the scanned function**.

**Counters (in preference order):**

1. **Don't inline-hook the scanned function.** Hook a *different* layer or patch *data*. Instead of hooking `IsDebuggerPresent`, patch `PEB.BeingDebugged` (A4) — no trampoline anywhere. Instead of `replace`-ing `NtQueryInformationProcess`, `Memory.patchCode` the **caller's check site** (A11).
2. **Stalker-based hooking** — dynamic recompilation runs from a relocated copy and leaves the original code bytes intact, so a checksum/first-byte scan of the *target* function passes. Higher overhead; reserve for hot, heavily-scanned functions.
3. If a dedicated thread CRC32s a guarded function (`process-memory.md` §2.5), hook the **checksum routine** itself (or its `Sleep`/compare), or feed it the original bytes via a read hook on its memory accessor.

## B7. Parent-process = python.exe / frida.exe

**How it detects:** parent isn't `explorer.exe` — it's `python.exe`/`frida.exe`. Overlaps `misc.md` §2.

**Counter:** the A14 parent-process hook — spoof `InheritedFromUniqueProcessId` to explorer's PID and filter `SystemProcessInformation` names. Note that `frida -f` still leaves frida as the parent, so spoof it or launch via a benign loader.

## B8. Analyst evasion checklist (consolidated)

| Goal | Action |
|---|---|
| Hook before checks fire | `frida -f target.exe -l bypass.js` (spawn suspended) |
| No console artifacts | `-o out.log` to tee logs to a file |
| No TCP 27042 | custom port `frida-server -l 127.0.0.1:8088` + `-H`, or embedded gadget (script mode) |
| Beat name/string/thread scans at once | **renamed/patched Frida build** (phantom-frida, 16 vectors: agent file, `frida_agent_main`, thread names, D-Bus service/interfaces/GTypes, temp paths, residual strings) — covers B1/B2/B4/B5 |
| Beat integrity checks (B6) | prefer **data patches** (PEB, out-params) and `Memory.patchCode` at the check site over inline API hooks; use Stalker for must-hook hot functions |
| Non-call instructions (RDTSC, direct PEB reads, CPUID) | `Interceptor` **cannot** catch them — use Stalker or static `Memory.patchCode` |

---

## Mitigation map (technique → Anti-Debug-DB file)

| Technique | File |
|---|---|
| IsDebuggerPresent / CheckRemoteDebuggerPresent / NtQueryInformationProcess / PEB BeingDebugged / NtGlobalFlag / heap flags / NtQuerySystemInformation(kernel-dbg) | `debug-flags.md` |
| RDTSC / GetTickCount / QueryPerformanceCounter / timeGetTime timing | `timing.md` |
| SetUnhandledExceptionFilter / VEH / RaiseException / guard-page | `exceptions.md` |
| INT3/INT2D/ICE/single-step/prefix/instruction-based | `assembly.md` |
| Software BP scan / code CRC / first-byte compare / HW breakpoints (GetThreadContext) / guard-page memory BP | `process-memory.md` |
| CloseHandle/NtClose / OpenProcess(csrss) / exclusive CreateFile / NtQueryObject(DebugObject) | `object-handles.md` |
| NtSetInformationThread(HideFromDebugger) / self-debug / BlockInput / SuspendThread / SwitchDesktop / OutputDebugString | `interactive.md` |
| FindWindow / parent-process / DbgSetDebugFilterState / DBG_PRINTEXCEPTION_C / NtYieldExecution / GetWriteWatch | `misc.md` |

> **Confidence:** A1–A6, A8 (OutputDebugString), A9 (API-based timing), A10, A13 (NtQueryObject), A14, B1–B5, B6 counters are high-confidence (direct DB mitigation text + verified Frida API). RDTSC-via-Stalker, CPUID, and A11–A12 exception checks under *pure* Frida are situational — behavior depends on whether a real debugger is also attached, and the DB itself notes these need debugger-specific handling. Re-verify exact x86-vs-x64 CONTEXT/PEB offsets against your target Windows build; prefer `Process.enumerateModules()` over manual PEB walks where possible.
