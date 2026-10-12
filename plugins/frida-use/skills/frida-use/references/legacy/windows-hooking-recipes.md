# Windows API hooking with Frida — recipes cheatsheet

Copy-paste hooks for the Win32/Native APIs that matter in malware analysis and RE:
files, registry, networking, crypto (dump plaintext/keys), and process injection.
All JS is meant to be loaded into a target via `frida -l`, `frida-trace`, or the
Python bindings. Frida **17.x** API throughout (the version `frida-tools` installs).

> **Wide strings:** any `…W` API takes UTF-16 — read with `readUtf16String()`. Any
> `…A` API takes a code-page string — read with `readAnsiString()`. Reading a `W`
> arg with `readUtf8String()` returns only the first character.

---

## 0. Frida 17 API change (read first if you're copying from blogs)

Frida **17.0.0** (May 2025) removed the **static** two-arg `Module.*` lookup
helpers. Most tutorials online still use the old forms — they **throw** on 17+:

| Pre-17 (legacy — in most blog posts) | Frida 17+ (current) |
|---|---|
| `Module.getExportByName('kernel32.dll','CreateFileW')` | `Process.getModuleByName('kernel32.dll').getExportByName('CreateFileW')` |
| `Module.findExportByName(null,'CreateFileW')` | `Module.getGlobalExportByName('CreateFileW')` (slow) |
| `Module.findExportByName('user32.dll','MessageBoxW')` | `Process.getModuleByName('user32.dll').findExportByName('MessageBoxW')` |
| `Module.enumerateExports('kernel32.dll')` | `Process.getModuleByName('kernel32.dll').enumerateExports()` |

`get*` throws on miss; `find*` returns `null`. If you must run one script across
mixed Frida versions, paste this shim once:

```js
function resolve(mod, exp) {
  return (typeof Process.getModuleByName === 'function')
    ? Process.getModuleByName(mod).getExportByName(exp)   // Frida 17+
    : Module.getExportByName(mod, exp);                   // legacy ≤16
}
```

The recipes below use the native 17 form directly.

---

## 1. Resolving exports & modules

```js
// Module handle: { name, base, size, path }
const k32 = Process.getModuleByName('kernel32.dll');   // throws if not loaded
console.log(k32.name, k32.base, k32.size, k32.path);

k32.getExportByName('CreateFileW')      // absolute address; throws if absent
k32.findExportByName('CreateFileW')     // address | null

// Don't know the owning DLL? Search globally (costly — avoid in hot paths):
Module.getGlobalExportByName('connect') // | findGlobalExportByName -> null

// Module by an address (e.g. a return address):
Process.findModuleByAddress(this.returnAddress)   // Module | null

// Load a DLL into the target on demand, then resolve from the returned object:
const win32u = Module.load('C:\\Windows\\System32\\win32u.dll');
win32u.getExportByName('NtUserGetForegroundWindow');

// Enumerate exports (instance method on 17+):
Process.getModuleByName('ws2_32.dll').enumerateExports()
  .filter(e => e.type === 'function')
  .forEach(e => console.log(e.name, e.address));   // {type, name, address}
```

> Export lookup needs the module **already loaded**. For a DLL you `Module.load()`
> yourself, resolve from the returned `Module`, not by name.

---

## 2. Interceptor.attach — onEnter / onLeave (the core)

`Interceptor.attach(target, callbacks[, data])` — `target` is a `NativePointer`;
returns a listener with `.detach()`.

- `onEnter(args)` — `args` is an array of `NativePointer`. **Writable**: `args[0] = ptr('0x1337')`.
- `onLeave(retval)` — `retval` is `NativePointer`-derived; `retval.replace(...)`.
- `this` is **per-invocation, thread-local** → stash state in `onEnter`, read it in `onLeave`.

Useful `this.*`: `this.returnAddress`, `this.context` (regs — assignable),
`this.threadId`, `this.depth`, and **Windows-only `this.lastError`** (the
`GetLastError` value, also assignable).

```js
Interceptor.attach(Process.getModuleByName('kernel32.dll').getExportByName('CreateFileW'), {
  onEnter(args) {
    this.path = args[0].readUtf16String();   // save for onLeave
    this.access = args[1].toInt32();
  },
  onLeave(retval) {
    // retval is the HANDLE; INVALID_HANDLE_VALUE == (HANDLE)-1
    console.log(`CreateFileW("${this.path}") -> ${retval}  GLE=${this.lastError}`);
  }
});
```

> **`retval` is recycled** across calls — never store the object. Deep-copy with
> `ptr(retval.toString())` if you must keep it.

---

## 3. Calling convention & wide-string cheats

**x64 (win64 ABI):** integer/pointer args 1–4 → `RCX, RDX, R8, R9`; args 5+ on the
stack; floats in XMM0–3. Frida's `args[0..n]` already abstract this — don't index
registers by hand.

**x86:** most Win32 APIs are `__stdcall`. Reading args via `Interceptor.attach`
needs **no** ABI, but for `Interceptor.replace` / `NativeFunction` /
`NativeCallback` you **must** pass `abi:'stdcall'` (or `thiscall`/`fastcall`).
On x64 there is only `win64` (the default).

```js
args[0].readUtf16String()       // "W" wide string (LPCWSTR) — UTF-16LE
args[0].readUtf16String(len)    // len in CHARACTERS if known
args[0].readAnsiString()        // "A" string (LPCSTR), code-page aware — Windows-only
args[0].readUtf8String()        // UTF-8 / plain char*
args[0].writeUtf16String('new') // overwrite in place (+NUL) — must fit the buffer!
```

---

## 4. Concrete hooks

### 4.1 CreateFileW — file opens

`HANDLE CreateFileW(lpFileName, dwDesiredAccess, dwShareMode, lpSecurityAttributes, dwCreationDisposition, dwFlagsAndAttributes, hTemplateFile)`

```js
const GENERIC_WRITE = 0x40000000;
Interceptor.attach(Process.getModuleByName('kernel32.dll').getExportByName('CreateFileW'), {
  onEnter(args) {
    this.name   = args[0].readUtf16String();
    this.access = args[1].toInt32();
    this.disp   = args[4].toInt32();   // CREATE_NEW=1 CREATE_ALWAYS=2 OPEN_EXISTING=3 ...
  },
  onLeave(retval) {
    const mode = (this.access & GENERIC_WRITE) ? 'W' : 'R';
    console.log(`[CreateFileW] ${mode} "${this.name}" disp=${this.disp} -> ${retval}`);
  }
});
```

### 4.2 ReadFile / WriteFile — dump I/O buffers

`WriteFile` buffer is valid in `onEnter`; `ReadFile` fills its buffer on return —
dump in `onLeave`.

```js
const k = Process.getModuleByName('kernel32.dll');

// WriteFile(hFile, lpBuffer, nBytes, lpNumberOfBytesWritten, lpOverlapped)
Interceptor.attach(k.getExportByName('WriteFile'), {
  onEnter(args) {
    const n = args[2].toInt32();
    console.log(`[WriteFile] ${n} bytes:`);
    console.log(hexdump(args[1], { length: Math.min(n, 0x80), header: true }));
  }
});

// ReadFile(hFile, lpBuffer, nBytes, lpNumberOfBytesRead, lpOverlapped)
Interceptor.attach(k.getExportByName('ReadFile'), {
  onEnter(args) { this.buf = args[1]; this.pRead = args[3]; },   // LPDWORD (NULL w/ overlapped)
  onLeave(retval) {
    if (retval.toInt32() === 0 || this.pRead.isNull()) return;
    const n = this.pRead.readU32();
    if (n > 0) { console.log(`[ReadFile] ${n} bytes`); console.log(hexdump(this.buf, { length: Math.min(n, 0x80) })); }
  }
});
```

### 4.3 Registry — RegOpenKeyExW / RegSetValueExW (advapi32.dll)

```js
const adv = Process.getModuleByName('advapi32.dll');

// LONG RegOpenKeyExW(hKey, lpSubKey, ulOptions, samDesired, phkResult)
Interceptor.attach(adv.getExportByName('RegOpenKeyExW'), {
  onEnter(args) { this.sub = args[1].isNull() ? '(null)' : args[1].readUtf16String(); },
  onLeave(retval) { console.log(`[RegOpenKeyExW] "${this.sub}" -> ${retval.toInt32()}`); }  // 0 == ERROR_SUCCESS
});

// LONG RegSetValueExW(hKey, lpValueName, Reserved, dwType, lpData, cbData)
const REG_SZ = 1, REG_EXPAND_SZ = 2, REG_DWORD = 4;
Interceptor.attach(adv.getExportByName('RegSetValueExW'), {
  onEnter(args) {
    const name = args[1].isNull() ? '(default)' : args[1].readUtf16String();
    const type = args[3].toInt32(), data = args[4], cb = args[5].toInt32();
    let val;
    if (type === REG_SZ || type === REG_EXPAND_SZ) val = data.readUtf16String();
    else if (type === REG_DWORD)                   val = data.readU32();
    else                                           val = `<${cb} bytes>`;
    console.log(`[RegSetValueExW] ${name} (type=${type}) = ${val}`);
  }
});
```

> `lpData` is `const BYTE*` — only decode as a string for `REG_SZ`/`REG_EXPAND_SZ`/
> `REG_MULTI_SZ`. Much of the registry API physically lives in `kernelbase.dll`
> (forwarded from `advapi32.dll`); resolving via `advapi32.dll` still works.

### 4.4 Process injection — VirtualAllocEx / WriteProcessMemory / CreateRemoteThread

The classic chain is `OpenProcess → VirtualAllocEx → WriteProcessMemory →
CreateRemoteThread`. `(HANDLE)-1` is the current-process pseudo-handle, so
`args[0].equals(ptr(-1))` tells self-alloc from remote-alloc.

```js
const k = Process.getModuleByName('kernel32.dll');

// LPVOID VirtualAllocEx(hProcess, lpAddress, dwSize, flAllocationType, flProtect)
Interceptor.attach(k.getExportByName('VirtualAllocEx'), {
  onEnter(args) {
    this.size = args[2].toInt32();
    this.prot = args[4].toInt32();
    this.remote = !args[0].equals(ptr(-1));
  },
  onLeave(retval) {
    const x = (this.prot & 0xf0) ? ' [EXECUTABLE]' : '';   // PAGE_EXECUTE* = 0x10..0x80
    console.log(`[VirtualAllocEx] remote=${this.remote} size=${this.size} prot=0x${this.prot.toString(16)}${x} -> ${retval}`);
  }
});

// BOOL WriteProcessMemory(hProcess, lpBaseAddress, lpBuffer, nSize, *lpNumberOfBytesWritten)
Interceptor.attach(k.getExportByName('WriteProcessMemory'), {
  onEnter(args) {
    const n = args[3].toInt32();
    console.log(`[WriteProcessMemory] -> remote ${args[1]}  ${n} bytes`);
    console.log(hexdump(args[2], { length: Math.min(n, 0x40) }));
  }
});

// HANDLE CreateRemoteThread(hProcess, lpThreadAttributes, dwStackSize, lpStartAddress, lpParameter, dwCreationFlags, lpThreadId)
Interceptor.attach(k.getExportByName('CreateRemoteThread'), {
  onEnter(args) {
    console.log(`[CreateRemoteThread] start=${args[3]} param=${args[4]}  *** INJECTION ***`);
    console.log('  caller:', this.returnAddress, Process.findModuleByAddress(this.returnAddress)?.name);
  }
});
```

> Stealthier variants live in `ntdll.dll`: hook `NtCreateThreadEx`,
> `RtlCreateUserThread`, `NtMapViewOfSection`, plus `QueueUserAPC` and
> `SetThreadContext` (thread hijacking).

### 4.5 LoadLibraryW / GetProcAddress (kernel32.dll)

```js
const k = Process.getModuleByName('kernel32.dll');

Interceptor.attach(k.getExportByName('LoadLibraryW'), {
  onEnter(args) { this.lib = args[0].readUtf16String(); },
  onLeave(retval) { console.log(`[LoadLibraryW] "${this.lib}" -> ${retval}`); }
});
// Also: LoadLibraryExW (args[0]=name, args[2]=flags); LoadLibraryA (readAnsiString)

// FARPROC GetProcAddress(hModule, lpProcName)  — lpProcName is ANSI, or an ordinal if hi-word == 0
Interceptor.attach(k.getExportByName('GetProcAddress'), {
  onEnter(args) {
    const p = args[1];
    this.name = (p.compare(ptr(0x10000)) < 0) ? `#${p.toInt32()}` : p.readAnsiString();  // import by ordinal
    this.hmod = args[0];
  },
  onLeave(retval) { console.log(`[GetProcAddress] ${this.hmod} ${this.name} -> ${retval}`); }
});
```

### 4.6 CreateProcessW — child processes (kernel32.dll)

`lpCommandLine` (arg[1]) is **mutable** — malware often rewrites it. `lpProcessInformation`
(arg[9]) is filled on return.

```js
Interceptor.attach(Process.getModuleByName('kernel32.dll').getExportByName('CreateProcessW'), {
  onEnter(args) {
    const app = args[0].isNull() ? '(null)' : args[0].readUtf16String();
    const cmd = args[1].isNull() ? '(null)' : args[1].readUtf16String();
    console.log(`[CreateProcessW] app="${app}" cmd="${cmd}"`);
    this.ppi = args[9];                       // LPPROCESS_INFORMATION
  },
  onLeave(retval) {
    if (retval.toInt32() !== 0 && !this.ppi.isNull()) {
      // PROCESS_INFORMATION { HANDLE hProcess; HANDLE hThread; DWORD dwProcessId; DWORD dwThreadId; }
      const pid = this.ppi.add(2 * Process.pointerSize).readU32();
      console.log(`   -> child PID ${pid}`);
    }
  }
});
```

### 4.7 HTTP — WinINet / WinHTTP (dump request bodies pre-encryption)

```js
// WinINet: request headers + optional POST body (wininet.dll)
const wi = Process.getModuleByName('wininet.dll');
Interceptor.attach(wi.getExportByName('HttpSendRequestW'), {
  onEnter(args) {
    const hdrLen = args[2].toInt32();
    const hdrs = args[1].isNull() ? '' : args[1].readUtf16String(hdrLen > 0 ? hdrLen : -1);
    const optLen = args[4].toInt32();
    console.log(`[HttpSendRequestW] headers:\n${hdrs}`);
    if (!args[3].isNull() && optLen > 0) {
      console.log('[HttpSendRequestW] body:');
      console.log(hexdump(args[3], { length: Math.min(optLen, 0x100) }));
    }
  }
});  // HttpSendRequestA -> readAnsiString for headers

// WinHTTP server name (winhttp.dll)
const wh = Process.getModuleByName('winhttp.dll');
Interceptor.attach(wh.getExportByName('WinHttpConnect'), {
  onEnter(args) { console.log('[WinHttpConnect]', args[1].readUtf16String(), 'port', args[2].toInt32()); }
});
```

> **Decrypted HTTPS:** hook `WinHttpWriteData`/`WinHttpReadData` (winhttp) or
> `InternetWriteFile`/`InternetReadFile` (wininet) — bodies there are plaintext.
> For raw TLS, hook Schannel's `EncryptMessage`/`DecryptMessage` in `sspicli.dll`.

### 4.8 Winsock — connect / send / recv (ws2_32.dll)

`send` buffer is valid in `onEnter`; `recv` fills its buffer on return.

```js
const ws2 = Process.getModuleByName('ws2_32.dll');

// int connect(SOCKET s, const struct sockaddr* name, int namelen)
Interceptor.attach(ws2.getExportByName('connect'), {
  onEnter(args) {
    const sa = args[1];
    if (sa.readU16() === 2) {                          // AF_INET
      const portBE = sa.add(2).readU16();              // sin_port — network byte order
      const port = ((portBE & 0xff) << 8) | (portBE >> 8);
      const ip = [0,1,2,3].map(i => sa.add(4 + i).readU8()).join('.');
      console.log(`[connect] ${ip}:${port}`);
    }
  }
});

// int send(SOCKET s, const char* buf, int len, int flags)
Interceptor.attach(ws2.getExportByName('send'), {
  onEnter(args) {
    const n = args[2].toInt32();
    console.log(`[send] ${n} bytes`);
    console.log(hexdump(args[1], { length: Math.min(n, 0x80) }));
  }
});

// int recv(SOCKET s, char* buf, int len, int flags)  — buf valid on leave
Interceptor.attach(ws2.getExportByName('recv'), {
  onEnter(args) { this.buf = args[1]; },
  onLeave(retval) {
    const n = retval.toInt32();
    if (n > 0) { console.log(`[recv] ${n} bytes`); console.log(hexdump(this.buf, { length: Math.min(n, 0x80) })); }
  }
});
```

> **Gotcha:** `WSASend`/`WSARecv` use `WSABUF` scatter/gather arrays, not a flat
> buffer — read `lpBuffers->buf` / `->len`. Many apps use these instead.

### 4.9 Crypto — dump plaintext/ciphertext (advapi32 / bcrypt.dll)

```js
// BOOL CryptEncrypt(hKey, hHash, Final, dwFlags, *pbData, *pdwDataLen, dwBufLen)
// Plaintext in pbData BEFORE the call; ciphertext in the SAME buffer AFTER (in-place).
const adv = Process.getModuleByName('advapi32.dll');
Interceptor.attach(adv.getExportByName('CryptEncrypt'), {
  onEnter(args) {
    this.pData = args[4]; this.pLen = args[5];          // DWORD* current length
    console.log('[CryptEncrypt] PLAINTEXT:');
    console.log(hexdump(this.pData, { length: Math.min(this.pLen.readU32(), 0x80) }));
  },
  onLeave() {
    console.log('[CryptEncrypt] CIPHERTEXT:');
    console.log(hexdump(this.pData, { length: Math.min(this.pLen.readU32(), 0x80) }));
  }
});

// NTSTATUS BCryptEncrypt(hKey, *pbInput, cbInput, *pPaddingInfo, *pbIV, cbIV, *pbOutput, cbOutput, *pcbResult, dwFlags)
const bc = Process.getModuleByName('bcrypt.dll');
Interceptor.attach(bc.getExportByName('BCryptEncrypt'), {
  onEnter(args) {
    const inLen = args[2].toInt32();
    console.log(`[BCryptEncrypt] PLAINTEXT (${inLen} bytes):`);
    if (!args[1].isNull() && inLen) console.log(hexdump(args[1], { length: Math.min(inLen, 0x80) }));
    this.pOut = args[6]; this.pcb = args[8];            // pbOutput, pcbResult
  },
  onLeave() {
    if (this.pOut.isNull() || this.pcb.isNull()) return;  // sizing call: pbOutput == NULL
    const n = this.pcb.readU32();
    console.log(`[BCryptEncrypt] CIPHERTEXT (${n} bytes):`);
    if (n) console.log(hexdump(this.pOut, { length: Math.min(n, 0x80) }));
  }
});
```

> **BCrypt double-call:** callers often invoke `BCryptEncrypt` once with
> `pbOutput == NULL` just to size the buffer, then again to encrypt. Guard with
> `isNull()` (shown) so you don't dump garbage. To grab the **symmetric key**,
> hook `BCryptGenerateSymmetricKey` (key material in `pbSecret`/`cbSecret`) or
> `BCryptImportKey`. The legacy CAPI equivalent is `CryptImportKey`/`CryptDeriveKey`.

**OpenSSL / bundled crypto (`libcrypto`, BoringSSL, NSS, …).** Many Windows apps —
and a lot of malware — ship their **own** crypto and never call CAPI/CNG, so the
hooks above see nothing. Hook the bundled library's exports instead. OpenSSL's
generic EVP layer (in `libcrypto-3-x64.dll`) carries the key, IV, and plaintext:

```js
// int EVP_CipherInit_ex(ctx, cipher, impl, const unsigned char* key, const unsigned char* iv, int enc)
//   key=args[3]  iv=args[4]  enc=args[5]  (1=encrypt, 0=decrypt, -1=unchanged)
// int EVP_CipherUpdate(ctx, out, int* outl, const unsigned char* in, int inl)
//   in=args[3]  inl=args[4]  (plaintext when encrypting; ciphertext when decrypting)
const lib = Process.getModuleByName('libcrypto-3-x64.dll');  // x86: libcrypto-3.dll; 1.1.x: libcrypto-1_1.dll
const KEY_LEN = 32, IV_LEN = 16;                             // AES-256-CBC; else query the ctx in onLeave

Interceptor.attach(lib.getExportByName('EVP_CipherInit_ex'), {
  onEnter(args) {
    if (!args[3].isNull()) { console.log('[EVP key]'); console.log(hexdump(args[3], { length: KEY_LEN })); }
    if (!args[4].isNull()) { console.log('[EVP iv]');  console.log(hexdump(args[4], { length: IV_LEN  })); }
  }
});
Interceptor.attach(lib.getExportByName('EVP_CipherUpdate'), {
  onEnter(args) {
    const n = args[4].toInt32();
    if (!args[3].isNull() && n > 0) { console.log(`[EVP in] ${n} bytes`); console.log(hexdump(args[3], { length: Math.min(n, 0x80) })); }
  }
});
```

This recovers a **password-derived** key that never appears on the command line.
Also hook `EVP_EncryptInit_ex`/`EVP_EncryptUpdate` (and the `Decrypt*` variants)
for callers that use those directly rather than the generic `Cipher*` ones.

> **Noise warning (important):** these generic EVP entry points are *also* used by
> OpenSSL's power-on self-tests and by KDFs (PBKDF2/HMAC), so one `openssl enc` run
> can fire them 100+ times — the self-test key is the giveaway constant
> `00 01 02 … 1f`. Filter to the real operation by input length, readable
> plaintext, or by stashing the `ctx` pointer (`args[0]`) to correlate init↔update.
> Discover a bundled lib's crypto exports with
> `Process.getModuleByName(dll).enumerateExports()` or `rz-bin -E lib.dll | grep -i EVP`.
> BoringSSL exports the same `EVP_*` names; NSS uses `PK11_Cipher`/`PK11_Encrypt`.

### 4.10 MessageBoxW — read + rewrite + force return value (user32.dll)

```js
// int MessageBoxW(HWND hWnd, LPCWSTR lpText, LPCWSTR lpCaption, UINT uType)
Interceptor.attach(Process.getModuleByName('user32.dll').getExportByName('MessageBoxW'), {
  onEnter(args) {
    console.log(`[MessageBoxW] text="${args[1].readUtf16String()}" caption="${args[2].readUtf16String()}"`);
    args[1].writeUtf16String('Hooked by Frida!');   // rewrite text in place (must fit)
  },
  onLeave(retval) { retval.replace(1); }            // pretend user clicked OK (IDOK=1)
});
```

---

## 5. Interceptor.replace + NativeFunction (substitute logic, call original)

Replace a function wholesale and chain to the original via a `NativeFunction`. On
**x86 set the ABI**; on x64 it's `win64` (default).

```js
const k = Process.getModuleByName('kernel32.dll');
const pCreateFileW = k.getExportByName('CreateFileW');

const origCreateFileW = new NativeFunction(
  pCreateFileW, 'pointer',
  ['pointer','uint','uint','pointer','uint','uint','pointer'],
  (Process.arch === 'ia32') ? { abi: 'stdcall' } : undefined);

Interceptor.replace(pCreateFileW, new NativeCallback(function (name, acc, sh, sa, disp, fl, tmpl) {
  const s = name.readUtf16String();
  if (s && s.toLowerCase().endsWith('secret.txt')) {
    console.log('[CreateFileW] BLOCKED', s);
    this.lastError = 2;          // ERROR_FILE_NOT_FOUND (settable in the callback)
    return ptr(-1);              // INVALID_HANDLE_VALUE
  }
  return origCreateFileW(name, acc, sh, sa, disp, fl, tmpl);
}, 'pointer', ['pointer','uint','uint','pointer','uint','uint','pointer'],
   (Process.arch === 'ia32') ? 'stdcall' : undefined));
// Undo: Interceptor.revert(pCreateFileW);
```

> `attach` observes/tweaks; `replace` substitutes. `Interceptor.replaceFast` is
> lower overhead but **returns** a pointer to the original you must call yourself.
> `SystemFunction` is like `NativeFunction` but returns `{ value, lastError }` on
> Windows — use it when you need the original call's `GetLastError`.

---

## 6. Calling a Win32 API yourself

```js
const user32 = Process.getModuleByName('user32.dll');
const MessageBoxW = new NativeFunction(
  user32.getExportByName('MessageBoxW'),
  'int', ['pointer','pointer','pointer','uint'],
  (Process.arch === 'ia32') ? { abi: 'stdcall' } : undefined);

const text = Memory.allocUtf16String('Hello from Frida');   // keep the JS refs alive!
const cap  = Memory.allocUtf16String('Frida');
MessageBoxW(NULL, text, cap, 0x40 /* MB_ICONINFORMATION */);
```

> `Memory.allocUtf16String`/`allocAnsiString`/`allocUtf8String` results are GC'd
> when the JS handle drops — hold a reference while native code uses the pointer.

---

## 7. Reading structs (PEB walk, UNICODE_STRING)

Use `.add(off)` + typed reads. Offsets below are **x64**; they differ on x86.
For plain module enumeration prefer `Process.enumerateModules()` over a manual walk.

```js
// RtlGetCurrentPeb() is exported by ntdll and returns the PEB pointer directly.
// (NtCurrentTeb is a compiler intrinsic reading gs:[0x30], NOT an ntdll export.)
const RtlGetCurrentPeb = new NativeFunction(Process.getModuleByName('ntdll.dll').getExportByName('RtlGetCurrentPeb'), 'pointer', []);
const peb = RtlGetCurrentPeb();

const beingDebugged = peb.add(0x002).readU8();   // PEB.BeingDebugged (BYTE)
const ntGlobalFlag  = peb.add(0x0bc).readU32();  // NtGlobalFlag (x64; x86: +0x68)
const ldr           = peb.add(0x018).readPointer();   // PEB_LDR_DATA*

// Walk InMemoryOrderModuleList (head LIST_ENTRY at PEB_LDR_DATA+0x20):
const head = ldr.add(0x20);
let cur = head.readPointer();
while (!cur.equals(head)) {
  const entry = cur.sub(0x10);                    // InMemoryOrderLinks is at LDR_DATA_TABLE_ENTRY+0x10
  const base  = entry.add(0x30).readPointer();    // DllBase
  const usLen = entry.add(0x48).readU16();        // FullDllName.Length (BYTES)
  const usBuf = entry.add(0x48 + 0x8).readPointer();
  const name  = usBuf.isNull() ? '(null)' : usBuf.readUtf16String(usLen / 2);
  console.log(base, name);
  cur = cur.readPointer();
}
```

> **UNICODE_STRING:** `Length` is in **bytes** — pass `Length/2` to
> `readUtf16String(chars)`, and `Buffer` is **not** guaranteed NUL-terminated, so
> always pass the explicit length. Verify offsets against your Win10/11 build.

---

## 8. Memory.scan / pattern scanning

Pattern: `"13 37 ?? ff"` (`??` = any byte), nibble wildcards `"?3 37"`, or r2 mask
`"13 37 : 1f ff"`.

```js
const m = Process.mainModule;                        // the main EXE

// Async (recommended for large ranges)
Memory.scan(m.base, m.size, '48 8b ?? ?? ?? ?? ?? 48 85 c0', {
  onMatch(address, size) { console.log('match @', address); /* return 'stop' to end early */ },
  onError(reason)  { console.log('scan error:', reason); },
  onComplete()     { console.log('scan done'); }
});

// Synchronous -> [{address, size}]; scope to executable ranges to skip unreadable sections
Process.getModuleByName('app.exe').enumerateRanges('r-x').forEach(({ base, size }) => {
  Memory.scanSync(base, size, '48 8b 05').forEach(h => console.log(h.address));
});
```

> Scanning a whole module hits non-readable sections — scope to executable ranges
> via `enumerateRanges('r-x')` (above) or handle `onError`. `ptr.toMatchPattern()`
> builds a scan pattern from a pointer value (find references to an address).

---

## 9. Stalker — coverage / instruction tracing

Heavy — scope to one thread and one module. Never enable `exec: true` broadly.

```js
const tid = Process.getCurrentThreadId();
const main = Process.mainModule;
const lo = main.base, hi = main.base.add(main.size);

Stalker.follow(tid, {
  events: { compile: true },                         // one event per basic block compiled — best for coverage
  onReceive(events) {
    const parsed = Stalker.parse(events, { annotate: true, stringify: false });
    // for 'compile' you get [start, end] block bounds
  }
  // or onCallSummary(summary) for a {targetAddr: count} call-frequency map
});
// ... run the code, then:
Stalker.unfollow(tid);
Stalker.flush();
```

> SEH-heavy code and self-modifying packers can destabilize Stalker. For
> DRcov-compatible coverage use the `compile`-event approach (or `frida-drcov`).

---

## 10. hexdump

```js
console.log(hexdump(args[1], { offset: 0, length: 64, header: true, ansi: false }));
// ansi:true colorizes (skip when piping to a file). Works on a NativePointer or an ArrayBuffer.
const buf = args[1].readByteArray(64);
console.log(hexdump(buf, { length: 64, header: true }));
```

Always `addr.isNull()`-check before `readByteArray`/`hexdump` on an arg that may be NULL.

---

## 11. Minimal Python harness

```python
import frida, sys

def on_message(message, data):
    if message['type'] == 'error':  print('[!] ' + message['stack'])
    elif message['type'] == 'send': print('[i] ' + str(message['payload']))
    else:                           print(message)

session = frida.attach(sys.argv[1])          # PID or process name, e.g. "notepad.exe"
with open('hooks.js', 'r', encoding='utf-8') as f:
    script = session.create_script(f.read())
script.on('message', on_message)
script.load()
print('[*] Hooked. Press Enter to detach.')
sys.stdin.read()
session.detach()
```

**Spawn a target *with arguments*** (most CLI tools need this — e.g. `openssl enc …`).
The Python API takes a clean argv **list** (no collision with frida's own flags),
and nothing runs until you `resume`, so hooks land before `main`:

```python
device = frida.get_local_device()
pid = device.spawn([r'C:\path\openssl.exe', 'enc', '-aes-256-cbc', '-in', 'a', '-out', 'b'])
session = device.attach(pid)
# ... create_script(...).load() — install hooks while the process is suspended ...
device.resume(pid)                 # release; hooks are already in place
```

From the CLI, dashed target args must come **after `--`** — otherwise frida parses
them as its own options (`frida: error: unrecognized arguments: -aes-256-cbc`):

```powershell
frida -f C:\path\openssl.exe -l hooks.js -- enc -aes-256-cbc -in a -out b
```

Or skip Python: `frida -n notepad.exe -l hooks.js` (attach) / `frida -f notepad.exe -l hooks.js`
(spawn — hooks load before the entry point, then auto-resumes; add `--pause` to hold for `%resume`).
`frida-trace -p <pid> -i "CreateFileW" -i "ws2_32.dll!send"`
auto-generates editable `onEnter`/`onLeave` stubs under `__handlers__\`.

---

## 12. Windows gotchas (consolidated)

- **API suffixes:** `…W` → `readUtf16String()`; `…A` → `readAnsiString()` (Windows-only). Wide read as UTF-8 returns one char.
- **Frida 17 broke static `Module.*`** — use `Process.getModuleByName(dll).getExportByName(fn)`.
- **x86 `__stdcall`:** set `abi:'stdcall'` for `NativeFunction`/`NativeCallback`/`replace`. x64 = `win64` (default). Reading args via `attach` needs no ABI.
- **GetLastError:** read/write `this.lastError` in the invocation context, or use `SystemFunction` for the original's error.
- **Output buffers fill on return:** `ReadFile`, `recv`, `RegQueryValueExW`, `CryptEncrypt` (in-place) → dump in `onLeave`.
- **HANDLE sentinels:** `INVALID_HANDLE_VALUE == (HANDLE)-1` → compare with `retval.equals(ptr(-1))`, not `toInt32()` (x64 pointer is 64-bit). Pseudo-handles: current process `(HANDLE)-1`, current thread `(HANDLE)-2`.
- **Forwarded exports:** many `kernel32`/`advapi32` names forward to `kernelbase.dll`/`ntdll.dll`. Resolving by the documented owning DLL still works; to hook the real implementation, resolve in `kernelbase.dll`/`ntdll.dll`.
- **Keep allocs alive:** `Memory.alloc*String` results are GC'd when the JS handle drops — hold a reference.
- **`Module.enumerateSymbols()` is unavailable on Windows** — use `DebugSymbol` for symbolication.

---

## Sources

- Local: `frida-website` docs — `javascript-api.md` (Module/Interceptor/NativeFunction/NativePointer/Memory.scan/Stalker), `examples/windows.md`, `functions.md`. Bundled here under `references/frida-docs/`.
- [Frida 17.0.0 Released](https://frida.re/news/2025/05/17/frida-17-0-0-released/) — static `Module.*` removal; [frida #3608](https://github.com/frida/frida/issues/3608).
- [Instrumenting Windows APIs with Frida — ired.team](https://www.ired.team/miscellaneous-reversing-forensics/windows-kernel-internals/instrumenting-windows-apis-with-frida).
- [Exploring Windows API Hooking with Frida — CyberGhost13337](https://cyberghost13337.github.io/2023/12/18/frida-for-windows-part1.html).
- [FuzzySecurity — Application Introspection & Hooking With Frida](https://fuzzysecurity.com/tutorials/29.html).

> PEB / `LDR_DATA_TABLE_ENTRY` / `UNICODE_STRING` offsets are **x64** and stable
> across Win10/11 but should be verified against the target build — prefer
> `Process.enumerateModules()` where possible.
