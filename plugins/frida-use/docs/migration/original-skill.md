---
name: frida-windows-re
description: >-
  Cheatsheet for dynamic instrumentation and security analysis of Windows
  binaries with Frida and the frida-tools CLI (frida REPL, frida-trace,
  frida-ps). Use it for runtime/dynamic analysis of a Windows process, DLL, or
  .exe: hooking and tracing Win32/Native (kernel32, ntdll, advapi32, ws2_32,
  bcrypt) API calls, intercepting/modifying args and return values, dumping
  decrypted buffers or crypto keys, monitoring file/registry/network I/O, tracing
  process injection (VirtualAllocEx/WriteProcessMemory/CreateRemoteThread),
  unpacking, and bypassing anti-debug / anti-Frida checks. Equally for
  product-security / app-pentest of legitimate thick clients: TLS/HTTPS
  interception and cert-pinning bypass, auditing secret/key handling (DPAPI,
  crypto), and license/auth checks. Reach for it on phrasings like "hook this
  Windows function", "intercept CreateFileW", "frida script for Windows", or any
  Frida / frida-trace mention. Complements rizin-windows-re (static RE).
---

# Frida on Windows — Security Analysis Cheatsheet

Frida is a dynamic instrumentation toolkit: it injects a JavaScript engine
(GumJS) into a running native process so you can hook functions, read/write
memory, trace execution, and rewrite behavior live — no recompilation, no source.
On Windows it serves two complementary jobs: **malware analysis** — watch what a
sample *actually* does, dump data after it's decrypted, neutralize anti-analysis
tricks — and **product security / app-pentest** — audit how legitimate thick
clients handle TLS, secrets, keys, auth, licensing, and untrusted input.

This skill is a **cheatsheet and an entry point to bundled reference docs**, not
a workflow. Skim the inline fast paths below, then open the matching reference
file for depth. Everything here targets **local Windows native analysis**
(x86/x64), not Android/iOS.

## Install the dependency

Install `frida-tools` in an isolated uv tool environment, then verify it:

```powershell
uv tool install frida-tools
frida --version
```

---

## 1. Mental model (read this once)

- **Local injection needs no server.** On Windows, `frida`/`frida-trace` inject
  directly into local processes. `frida-server` is only for *remote* targets
  (Android/iOS/another box over `-H host:port`). `frida-gadget` is for embedding.
- **Spawn vs attach.**
  - `frida -f program.exe -l hooks.js` — **spawn**: Frida loads your script while
    the process is held suspended, so hooks land *before* the first instruction
    (packers, early anti-debug), then it **auto-resumes**. Add `--pause` to hold at
    the REPL prompt instead and release manually with `%resume`.
  - `frida <name|PID>` — **attach** to an already-running process.
- **Match the architecture.** A 64-bit Frida injects both 32- and 64-bit local
  targets; a 32-bit-only target needs a 32-bit frida-tools install (see the
  README). Check `Process.arch` once attached.
- **Run elevated** for system/protected processes (launch the terminal "as
  Administrator"). PPL/protected processes may still refuse injection.
- **Wide strings everywhere.** Win32 `...W` APIs take UTF-16 (`LPCWSTR`):
  read them with `arg.readUtf16String()`, *not* `readUtf8String()`. `...A` APIs
  use `readAnsiString()`.

---

## 2. CLI fast paths

```powershell
# Discover targets
frida-ps                       # all local processes (name + PID)
frida-ps -a                    # only applications (with a main window)
frida-ls-devices               # local + connected/remote devices

# REPL: attach and explore interactively
frida notepad.exe              # attach by name
frida -p 1234                  # attach by PID
frida -f malware.exe --pause   # spawn, hold at prompt (type %resume); without --pause it auto-runs
frida -f app.exe -l hooks.js   # spawn + load script (auto-reloads on save)
frida -f openssl.exe -l hooks.js -- enc -aes-256-cbc -in a -out b  # pass argv to the target: dashed args AFTER --
frida -p 1234 -l hooks.js -o session.log   # tee all output to a file

# Trace API calls with zero scripting (auto-generates editable handlers)
frida-trace -p 1234 -i "CreateFileW" -i "ReadFile" -i "WriteFile"
frida-trace -f app.exe -i "ws2_32.dll!*" -i "*!*Crypt*"   # by module / glob
frida-trace -p 1234 -I "ntdll.dll"      # include every export of a module (-X excludes)

# Other tools
frida-discover -f app.exe      # find hot functions worth tracing
frida-kill 1234                # kill via Frida's device layer
```

Inside the REPL, **`%` magic commands** drive the session — the full set is just
`%resume`, `%load <path>`, `%reload`, `%unload`, `%exec <path>`, `%time <expr>`,
`%autoperform`, `%autoreload`, `%help`. (There is **no** `%hexdump`/`%dump`/`%dis`/
`%backtrace` — hexdump is automatic for binary values, and symbolication/disasm is
done inline in JS: `hexdump(ptr)`, `DebugSymbol.fromAddress(ptr)`, `Instruction.parse(ptr)`.)
Full tool/flag/magic tables → **[references/cli-tools.md](references/cli-tools.md)**.
frida-trace's include/exclude syntax and the `__handlers__` editing workflow →
**[references/frida-docs/frida-trace.md](references/frida-docs/frida-trace.md)**.

---

## 3. The one hook you'll write most

The universal pattern: resolve an export → `Interceptor.attach` → read args in
`onEnter` (stash on `this`) → act in `onLeave`.

```js
// CreateFileW: log every file the target opens
const k32 = Process.getModuleByName('kernel32.dll');     // -> Module object
Interceptor.attach(k32.getExportByName('CreateFileW'), { // -> NativePointer
  onEnter(args) {
    this.path = args[0].readUtf16String();   // LPCWSTR (wide!)
  },
  onLeave(retval) {
    console.log(`CreateFileW("${this.path}") -> ${retval}`);
  }
});

// Search ALL modules for an export (when you don't know which DLL):
Interceptor.attach(Module.getGlobalExportByName('connect'), { /* ... */ });
```

Current Frida (17.x) API: `Process.getModuleByName(name)` returns a `Module`,
and you call `.getExportByName(fn)` **on it**, or
`Module.getGlobalExportByName(fn)` to search globally. (The old
`Module.getExportByName('kernel32.dll','CreateFileW')` two-arg form seen in most
blog posts was **removed in Frida 17** and now throws — see
[references/windows-hooking-recipes.md](references/windows-hooking-recipes.md) §0
for a version-safe shim.)

Other essentials, copy-paste ready:

```js
retval.replace(0);                      // force a return value (e.g. fake "not found")
args[0] = Memory.allocUtf16String('x'); this.keep = args[0];  // replace a wide-string arg (keep it alive!)
Interceptor.replace(addr, new NativeCallback(() => 1, 'int', []));  // stub a whole function
const fn = new NativeFunction(addr, 'int', ['pointer','uint32'], 'stdcall'); // call native (x86 WINAPI = 'stdcall'; x64 = default/'win64')
console.log(hexdump(ptr, { length: 64, ansi: true }));         // dump a buffer
console.log(Thread.backtrace(this.context, Backtracer.ACCURATE)
  .map(DebugSymbol.fromAddress).join('\n'));                   // who called me
```

Hooking recipes for files, registry, networking, crypto (dump keys/plaintext),
and process-injection APIs → **[references/windows-hooking-recipes.md](references/windows-hooking-recipes.md)**.
Apps that bundle their own crypto (OpenSSL `libcrypto`, BoringSSL, NSS) don't call
Windows CAPI/CNG — hook the library's own exports instead (e.g. OpenSSL's
`EVP_CipherInit_ex`/`EVP_CipherUpdate`); see that file's §4.9.
Condensed API reference (Process/Module/Memory/NativePointer/Interceptor/
NativeFunction with exact signatures) →
**[references/js-api-cheatsheet.md](references/js-api-cheatsheet.md)**.

---

## 4. Anti-debug / anti-Frida bypass

Malware and packers check for debuggers and for Frida itself. Defeat them from a
Frida script — usually by hooking the detection API and lying, or patching the
flag it reads.

```js
// IsDebuggerPresent / CheckRemoteDebuggerPresent -> always "no debugger"
const k32 = Process.getModuleByName('kernel32.dll');
Interceptor.replace(k32.getExportByName('IsDebuggerPresent'),
  new NativeCallback(() => 0, 'int', []));

// PEB!BeingDebugged byte (read directly to dodge API hooks)
// 64-bit PEB+0x02; clear it once at startup.
```

For `NtQueryInformationProcess` (ProcessDebugPort=7, ProcessDebugObjectHandle=0x1e,
ProcessDebugFlags=0x1f), thread-hiding (`NtSetInformationThread` /
ThreadHideFromDebugger=0x11), timing checks (RDTSC/GetTickCount/QPC), hardware
breakpoints, and **anti-Frida** signals (thread names like `gum-js-loop`/`gmain`,
TCP port `27042`, in-memory `"frida"`/`"FridaScriptEngine"` strings, inline-hook
integrity checks) plus stealth tactics →
**[references/anti-debug-bypass.md](references/anti-debug-bypass.md)**.

The bundled **Check Point Anti-Debug encyclopedia** documents each detection
trick in detail (detection + mitigation), organized by mechanism →
**[references/anti-debug-db/](references/anti-debug-db/)** (`debug-flags.md`,
`timing.md`, `exceptions.md`, `process-memory.md`, `object-handles.md`,
`assembly.md`, `interactive.md`, `misc.md`).

---

## 5. Product security / app-pentest

The same hooks audit *legitimate* software you're authorized to test: read an app's
TLS plaintext, dump the secrets/keys it handles, prove a client-side license/auth
gate is bypassable, and map its input attack surface.

```js
// TLS plaintext from any app on the OS stack (Schannel) — DecryptMessage unseals in place:
const ssp = Process.getModuleByName('sspicli.dll');
Interceptor.attach(ssp.getExportByName('DecryptMessage'),
  { onEnter(a){ this.d = a[1]; }, onLeave(){ /* dump the SECBUFFER_DATA (type 1) buffer of this.d */ } });

// Force a client-side validator (license/auth) — find the RVA statically, then make it return true:
Interceptor.attach(Process.getModuleByName('app.exe').base.add(0x12a40),
  { onLeave(r){ r.replace(1); } });           // isLicensed()/checkAuth() -> true
```

Covers TLS/HTTPS interception + cert-pinning bypass (Schannel, bundled OpenSSL/NSS,
`CertVerifyCertificateChainPolicy`/`WinVerifyTrust`), secret & key auditing (DPAPI
`CryptUnprotectData`, Credential Manager, a crypto audit that flags weak algos /
static IVs / hardcoded keys), auth/license/integrity bypass, sensitive-data flow
(clipboard, env, named-pipe IPC, JWT/PII detection), and attack-surface mapping +
Stalker coverage for fuzzing → **[references/product-security.md](references/product-security.md)**.

---

## 6. Reference map — what to open when

| Need | Open |
|------|------|
| Every `frida-*` tool, its flags, and all REPL `%` magics | [references/cli-tools.md](references/cli-tools.md) |
| Terse JS API reference (security-focused) | [references/js-api-cheatsheet.md](references/js-api-cheatsheet.md) |
| Copy-paste Win32/Native hooking recipes | [references/windows-hooking-recipes.md](references/windows-hooking-recipes.md) |
| Defeat anti-debug / anti-Frida checks | [references/anti-debug-bypass.md](references/anti-debug-bypass.md) |
| The detection tricks themselves, in depth | [references/anti-debug-db/](references/anti-debug-db/) |
| Product-security / app-pentest recipes (TLS interception, cert-pinning bypass, secret/key audit, auth/license bypass, fuzzing) | [references/product-security.md](references/product-security.md) |
| `frida-trace` deep dive + handler editing | [references/frida-docs/frida-trace.md](references/frida-docs/frida-trace.md) |
| **Canonical** full JS API (every class/method) | [references/frida-docs/javascript-api.md](references/frida-docs/javascript-api.md) |
| Stalker — instruction/branch tracing & coverage | [references/frida-docs/stalker.md](references/frida-docs/stalker.md) |
| `send()`/`recv()`/`rpc.exports` messaging (script ⇄ Python) | [references/frida-docs/messages.md](references/frida-docs/messages.md) |
| String/arg-replacement pitfalls (keep-alive, read-only) | [references/frida-docs/best-practices.md](references/frida-docs/best-practices.md) |
| NativeFunction/Callback type & ABI details | [references/frida-docs/functions.md](references/frida-docs/functions.md) |
| Gadget (embed Frida without injection) | [references/frida-docs/gadget.md](references/frida-docs/gadget.md) |
| Worked Windows example (hook by IDA address) | [references/frida-docs/examples-windows.md](references/frida-docs/examples-windows.md) |
| REPL intro / quickstart / modes / troubleshooting | [references/frida-docs/](references/frida-docs/) |

### Typical pairing with static RE
Use **rizin-windows-re** (or IDA/Ghidra) to locate an interesting function or
offset statically, then hook it here. For an internal (unexported) function at
file offset `0xXXXX`:

```js
const base = Process.getModuleByName('target.exe').base;
Interceptor.attach(base.add(0x14230), { onEnter(args) { /* ... */ } });
```

---

## Provenance

Bundled references are copied/adapted from local, openly-licensed sources:
`references/frida-docs/` from the **Frida website docs** (frida.re,
`oleavr` et al.); `references/anti-debug-db/` from the **Anti-Debug-DB / Check
Point Anti-Debug encyclopedia** (MIT). The synthesized cheatsheets
(`cli-tools.md`, `js-api-cheatsheet.md`, `windows-hooking-recipes.md`,
`anti-debug-bypass.md`, `product-security.md`) distill those plus the
`frida-tools` source, Microsoft Learn (Win32/SSPI/DPAPI/
wincrypt/wintrust), OpenSSL/NSS docs, and public Frida appsec write-ups; every
Windows export↔DLL pairing was checked against live System32 DLLs (`rz-bin`).
