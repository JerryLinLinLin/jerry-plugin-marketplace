# Product security / app-pentest recipes (Frida on Windows)

Frida for legitimate Windows software —
thick clients, desktop apps, in-house products. Same engine as the malware sheet,
opposite intent: prove how an app handles secrets, TLS, auth, licensing, and
input. Recipes are Frida **17.x**, copy-paste, and cross-reference
[windows-hooking-recipes.md](windows-hooking-recipes.md) (core hooking) and
[anti-debug-bypass.md](anti-debug-bypass.md).

## 0. Ground rules

- **Spawn to catch one-shot/early calls** (`GetCommandLineW`, license checks, init
  crypto): `frida -f app.exe -l hooks.js` — and pass target argv after `--`
  (`-- /login --user x`). Attach (`frida -n app.exe`) for already-running state.
- **Force a result** with `retval.replace(ptr(N))` (pass a `NativePointer`, not a
  bare number). **Read output buffers in `onLeave`**, input buffers in `onEnter`.
- **Resolve defensively:** `getExportByName` *throws* on miss — use
  `findExportByName` (→`null`) when a DLL/export may be absent (bundled libs,
  version drift), and `Process.findModuleByName` for optional modules.
- **App-owned functions have no export name** — find them statically (rizin /
  [rizin-windows-re], IDA), then hook `module.base.add(rva)` (ASLR-safe; never a
  hardcoded VA). Pairs with the static-RE skill.

---

## 1. TLS / HTTPS interception + cert-pinning bypass

Goal: read an app's TLS plaintext and make its handshake to your Burp/mitmproxy CA
succeed. Capture happens above/below the cipher; pinning bypass forces validation OK.

### 1.1 Schannel plaintext — `EncryptMessage` (out) / `DecryptMessage` (in)

Covers any app on the OS TLS stack (WinHTTP, WinINet, .NET `SslStream`, most native
clients). Plaintext lives in the `SECBUFFER_DATA` (type 1) buffer of the `SecBufferDesc`.

```js
const SECBUFFER_DATA = 1;                         // x64 layout: SecBufferDesc{ver@0,cBuffers@4,pBuffers@8}; SecBuffer{cb@0,type@4,pv@8} stride 16
function dumpData(pDesc, tag) {
  if (pDesc.isNull()) return;
  const n = pDesc.add(4).readU32();
  let p = pDesc.add(8).readPointer();
  for (let i = 0; i < n; i++, p = p.add(16)) {
    if (p.add(4).readU32() !== SECBUFFER_DATA) continue;
    const cb = p.readU32(), buf = p.add(8).readPointer();
    if (cb > 0 && !buf.isNull()) { console.log(`[${tag}] ${cb} bytes`); console.log(hexdump(buf, { length: Math.min(cb, 0x100) })); }
  }
}
const ssp = Process.getModuleByName('sspicli.dll');           // resolve here, NOT secur32 (which only forwards)
Interceptor.attach(ssp.getExportByName('EncryptMessage'), { onEnter(a){ dumpData(a[2], 'TLS OUT'); } });   // plaintext BEFORE seal
Interceptor.attach(ssp.getExportByName('DecryptMessage'), { onEnter(a){ this.d = a[1]; }, onLeave(){ dumpData(this.d, 'TLS IN'); } });
```

> `DecryptMessage` unseals **in place**, so read in `onLeave` (in `onEnter` it's
> still ciphertext). Iterate buffers — only type 1 is plaintext; it can legitimately
> be empty (`cb>0` guard). Captures plaintext only; pair with §1.4 to complete the
> handshake to your proxy. **x86:** `SecBuffer` stride is 12 (4-byte `pvBuffer`).

### 1.2 HTTP bodies above TLS — WinHTTP / WinINet

Whole request/response bodies (cleaner than record-sized Schannel chunks). Headers
are in [windows-hooking-recipes.md §4.7](windows-hooking-recipes.md).

```js
const wh = Process.getModuleByName('winhttp.dll');
Interceptor.attach(wh.getExportByName('WinHttpWriteData'), {   // request body — onEnter
  onEnter(a){ const n=a[2].toInt32(); if(n>0){ console.log(`[WinHttpWriteData] ${n}b`); console.log(hexdump(a[1],{length:Math.min(n,0x200)})); } }
});
Interceptor.attach(wh.getExportByName('WinHttpReadData'), {    // response body — onLeave
  onEnter(a){ this.buf=a[1]; this.pRead=a[3]; },               // lpdwNumberOfBytesRead
  onLeave(r){ if(r.toInt32()===0||this.pRead.isNull()) return; const n=this.pRead.readU32();
    if(n>0){ console.log(`[WinHttpReadData] ${n}b`); console.log(hexdump(this.buf,{length:Math.min(n,0x200)})); } }
});
```

> Logging ≠ MITM — set the WinHTTP/OS proxy to Burp and bypass validation with §1.4.
> Responses arrive in fragments (correlate by `hRequest`=`args[0]`); bodies may be
> gzip/br (decompress offline). `WinINet` = `InternetWriteFile`/`InternetReadFile`.
> .NET 5+ default `SocketsHttpHandler` uses sockets+Schannel (use §1.1), not WinHTTP.

### 1.3 Bundled OpenSSL — `SSL_read`/`SSL_write` + force-trust

For apps shipping their own OpenSSL (Electron/Node, curl, Qt, many finance/game
clients) the Schannel hooks see nothing.

```js
const ssl = Process.getModuleByName('libssl-3-x64.dll');      // name varies — confirm via Process.enumerateModules()
Interceptor.attach(ssl.getExportByName('SSL_write'), { onEnter(a){ const n=a[2].toInt32(); if(n>0){ console.log(`[SSL_write] ${n}b`); console.log(hexdump(a[1],{length:Math.min(n,0x200)})); } } });
Interceptor.attach(ssl.getExportByName('SSL_read'),  { onEnter(a){ this.b=a[1]; }, onLeave(r){ const n=r.toInt32(); if(n>0){ console.log(`[SSL_read] ${n}b`); console.log(hexdump(this.b,{length:Math.min(n,0x200)})); } } });
Interceptor.attach(ssl.getExportByName('SSL_get_verify_result'), { onLeave(r){ if(r.toInt32()!==0){ r.replace(ptr(0)); } } });  // X509_V_OK==0
```

> OpenSSL convention: **0 = success** (`X509_V_OK`). `SSL_get_verify_result` alone
> is often insufficient — also force `SSL_set_verify(ssl, mode=args[1], cb)` mode to
> `0` (`SSL_VERIFY_NONE`) or `X509_verify_cert` (libcrypto) → `replace(ptr(1))`.
> **BoringSSL** (Electron/Chromium) verifies via `SSL_CTX_set_custom_verify` — its
> callback returns `ssl_verify_ok` = `0`. Statically-linked OpenSSL has no exports
> (pattern-scan / `DebugSymbol`). Find names: `module.enumerateExports()` filtered for `SSL_`.

### 1.4 OS cert-validation / pinning bypass — `CertVerifyCertificateChainPolicy`, `WinVerifyTrust`

```js
const c32 = Process.getModuleByName('crypt32.dll');
Interceptor.attach(c32.getExportByName('CertVerifyCertificateChainPolicy'), {
  onEnter(a){ this.pStatus = a[3]; },                          // PCERT_CHAIN_POLICY_STATUS
  onLeave(retval){
    if (this.pStatus.isNull()) return;
    if (this.pStatus.add(4).readU32() !== 0) {                 // dwError@+4 is the REAL verdict
      this.pStatus.add(4).writeU32(0);                         // ERROR_SUCCESS
      this.pStatus.add(8).writeS32(-1); this.pStatus.add(12).writeS32(-1);  // lChainIndex/lElementIndex = whole chain, no error
    }
    retval.replace(ptr(1));
  }
});
// Authenticode / WinTrust chain trust (TLS via HTTPSPROV, or PE signature):
const wt = Process.getModuleByName('wintrust.dll');
['WinVerifyTrust','WinVerifyTrustEx'].forEach(fn => { const p = wt.findExportByName(fn); if(!p) return;
  Interceptor.attach(p, { onLeave(r){ if(r.toInt32()!==0) r.replace(ptr(0)); } });  // LONG: 0==trusted
});
```

> **Counter-intuitive (MS docs):** `CertVerifyCertificateChainPolicy` returns **TRUE
> even on a failed cert** — the verdict is `pPolicyStatus->dwError` (offset +4). Just
> `replace(ptr(1))` does nothing; you must zero +4. `WinVerifyTrust` returns a `LONG`
> where **only 0 = trusted** (not `SUCCEEDED()`); a blanket force also trusts
> tampered/unsigned binaries — scope to the action GUID (`args[1]`) if you only want
> TLS. Neither covers an app that hashes a pinned cert/SPKI itself (no OS call — see §3.4).

### 1.5 Bundled NSS (Gecko/Firefox-derived) — `PR_Read`/`PR_Write`, `SSL_AuthCertificateHook`

```js
function resolve(name){ for(const dll of ['nss3.dll','nspr4.dll']){ try{ return Process.getModuleByName(dll).getExportByName(name); }catch(e){} } throw new Error('no '+name); }
Interceptor.attach(resolve('PR_Write'), { onEnter(a){ const n=a[2].toInt32(); if(n>0){ console.log(`[PR_Write] ${n}b`); console.log(hexdump(a[1],{length:Math.min(n,0x200)})); } } });
Interceptor.attach(resolve('PR_Read'),  { onEnter(a){ this.b=a[1]; }, onLeave(r){ const n=r.toInt32(); if(n>0){ console.log(`[PR_Read] ${n}b`); console.log(hexdump(this.b,{length:Math.min(n,0x200)})); } } });
// trust bypass: force the app's SSLAuthCertificate cb to SECSuccess(0), or hook nss3!CERT_VerifyCertificate -> replace(ptr(0))
```

> Modern NSS **merges NSPR into `nss3.dll`** (no `nspr4.dll`) — the `resolve()` helper
> tries both. **NSS inverts the convention: `SECSuccess` = 0, `SECFailure` = -1**
> (opposite of OpenSSL). `CERT_VerifyCertNow` is *not* exported — use `CERT_VerifyCertificate`.
> `PR_Read`/`PR_Write` also carry non-TLS fd I/O — filter by `fd`=`args[0]`.

---

## 2. Secret & key auditing

### 2.1 DPAPI tap — `CryptProtectData` / `CryptUnprotectData` (crypt32)

See the cleartext an app protects (before encrypt) and unprotects (after decrypt):
saved passwords, tokens, vault keys, plus the optional entropy.

```js
const c32 = Process.getModuleByName('crypt32.dll');
const PTR = Process.pointerSize;                                // DATA_BLOB{cbData@0; pbData@8 x64/@4 x86}
function blob(p){ if(p.isNull()) return null; const n=p.readU32(), d=p.add(PTR).readPointer(); return (n&&!d.isNull())?{n,d}:null; }
function dump(t,b){ if(b){ console.log(`${t} (${b.n}B):`); console.log(hexdump(b.d,{length:Math.min(b.n,0x80)})); } }
Interceptor.attach(c32.getExportByName('CryptProtectData'),   { onEnter(a){ dump('[Protect] PLAINTEXT IN', blob(a[0])); dump('  entropy', blob(a[2])); } });
Interceptor.attach(c32.getExportByName('CryptUnprotectData'), { onEnter(a){ this.out=a[6]; }, onLeave(r){ if(r.toInt32()) dump('[Unprotect] PLAINTEXT OUT', blob(this.out)); } });
```

> Protect plaintext = **input** blob (`onEnter`); Unprotect plaintext = **output**
> blob `args[6]` (`onLeave`, only if `retval!=0` — both return BOOL). Wrong `pbData`
> offset dumps garbage. Also hook `CryptUnprotectMemory(pDataIn, cbDataIn, dwFlags)`
> (decrypts in place → dump `args[0]` for `args[1]` bytes, `onLeave`).

### 2.2 Credential Manager — `CredReadW` / `CredWriteW` (advapi32)

```js
const adv = Process.getModuleByName('advapi32.dll');
const ws = p => p.isNull() ? '(null)' : p.readUtf16String();
function showCred(c, where){ if(c.isNull()) return;            // CREDENTIALW x64 offsets:
  const type=c.add(0x04).readU32(), tgt=ws(c.add(0x08).readPointer());      // Type@4, TargetName@8
  const n=c.add(0x20).readU32(), blob=c.add(0x28).readPointer(), user=ws(c.add(0x48).readPointer());  // BlobSize@0x20, Blob@0x28, User@0x48
  console.log(`[${where}] target="${tgt}" user="${user}" type=${type} blobLen=${n}`);
  if(n && !blob.isNull()) console.log(hexdump(blob,{length:Math.min(n,0x100)}));   // type 2 (DOMAIN_PASSWORD) = UTF-16 password
}
Interceptor.attach(adv.getExportByName('CredReadW'),  { onEnter(a){ this.pp=a[3]; }, onLeave(r){ if(r.toInt32() && !this.pp.isNull()) showCred(this.pp.readPointer(),'CredReadW'); } });
Interceptor.attach(adv.getExportByName('CredWriteW'), { onEnter(a){ showCred(a[0],'CredWriteW'); } });
```

> `CredReadW` `args[3]` is `PCREDENTIALW*` — **deref once** (`readPointer()`), only on
> `retval!=0`. `CredentialBlob` is **not** NUL-terminated; `CredentialBlobSize` is in
> bytes. `CredEnumerateW` returns an array (`*Count`=`args[2]`, `**Creds`=`args[3]`).
> **x86** offsets differ (BlobSize@0x18, Blob@0x1C, User@0x28).

### 2.3 Crypto inventory + weak-algorithm flagger (CNG)

```js
const bc = Process.getModuleByName('bcrypt.dll');
const WEAK = /^(DES|DESX|3DES|3DES_112|RC2|RC4|MD2|MD4|MD5|SHA1)$/i;
Interceptor.attach(bc.getExportByName('BCryptOpenAlgorithmProvider'), { onEnter(a){ const alg=a[1].isNull()?'?':a[1].readUtf16String(); console.log(`[CNG alg] ${alg}`+(WEAK.test(alg)?'  *** WEAK ***':'')); } });
Interceptor.attach(bc.getExportByName('BCryptSetProperty'), { onEnter(a){ const prop=a[1].isNull()?'':a[1].readUtf16String(), val=a[2].isNull()?'':a[2].readUtf16String(); if(/chainingmode/i.test(prop)) console.log(`[CNG mode] ${val}`+(/ECB/i.test(val)?'  *** ECB ***':'')); } });
Interceptor.attach(bc.getExportByName('BCryptGenerateSymmetricKey'), { onEnter(a){ const n=a[5].toInt32(); console.log(`[CNG key] ${n}B:`); if(!a[4].isNull()&&n) console.log(hexdump(a[4],{length:Math.min(n,64)})); } });   // pbSecret=args[4]
```

> `pszAlgId` is **wide** (`readUtf16String`). Callers can pass `BCRYPT_*_ALG_HANDLE`
> pseudo-handles and **skip `OpenAlgorithmProvider`** — also key off gen/encrypt/hash.
> `AES` alone isn't weak — the **ECB** `SetProperty` flag is the finding. `pbSecret`
> (`args[4]`) is the final key; a constant across runs = **hardcoded key**. Legacy CAPI:
> `advapi32!CryptCreateHash` (ALG_ID = `args[2]`: `CALG_MD5`=0x8003, `CALG_SHA1`=0x8004).

### 2.4 Static / zero / reused IV detector

```js
function ivHash(p,n){ let h=0; for(let i=0;i<n;i++) h=((h*31)+p.add(i).readU8())>>>0; return h; }
function isZero(p,n){ for(let i=0;i<n;i++) if(p.add(i).readU8()!==0) return false; return true; }
const seen = {};
function checkIV(tag,p,n){ if(p.isNull()||!n) return; const z=isZero(p,n), h=ivHash(p,n).toString(16), dup=seen[h];
  seen[h]=(seen[h]||0)+1; console.log(`[${tag}] IV(${n}B) h=${h}`+(z?'  *** ALL-ZERO ***':'')+(dup?'  *** REUSED ***':'')); }
const bc = Process.getModuleByName('bcrypt.dll');
Interceptor.attach(bc.getExportByName('BCryptEncrypt'), { onEnter(a){ checkIV('BCryptEncrypt', a[4], a[5].toInt32()); } });   // pbIV=args[4]; read onEnter (updated in place)
const cl = Process.findModuleByName('libcrypto-3-x64.dll');
if(cl) Interceptor.attach(cl.getExportByName('EVP_EncryptInit_ex'), { onEnter(a){ if(!a[4].isNull()) checkIV('EVP_EncryptInit', a[4], 16); } });   // iv=args[4]
```

> `BCryptEncrypt` updates `pbIV` **in place** for chaining modes — read `onEnter`.
> For **AEAD (GCM/CCM)** the nonce is in `BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO`
> (`pPaddingInfo`=`args[3]`), not `pbIV` — a nonce repeat under one key is catastrophic.
> OpenSSL EVP key/iv may be NULL in a first init and set in a later one.

### 2.5 Capture password-derived keys at the KDF (PBKDF2)

```js
const bc = Process.getModuleByName('bcrypt.dll');
Interceptor.attach(bc.getExportByName('BCryptDeriveKeyPBKDF2'), {
  onEnter(a){ this.iter=a[5].toInt32(); this.dk=a[6]; this.dkn=a[7].toInt32(); const sN=a[4].toInt32();
    console.log(`[PBKDF2] iter=${this.iter}`+(this.iter<10000?'  *** LOW ITER ***':'')+(sN===0?'  *** EMPTY SALT ***':'')); },
  onLeave(r){ if(r.toInt32()===0 && !this.dk.isNull() && this.dkn){ console.log(`[PBKDF2] KEY (${this.dkn}B):`); console.log(hexdump(this.dk,{length:Math.min(this.dkn,64)})); } }  // NTSTATUS STATUS_SUCCESS==0
});
const cl = Process.findModuleByName('libcrypto-3-x64.dll');
if(cl) Interceptor.attach(cl.getExportByName('PKCS5_PBKDF2_HMAC'), {   // pass,passlen,salt,saltlen,iter,digest,keylen,out
  onEnter(a){ this.out=a[7]; this.kl=a[6].toInt32(); },
  onLeave(r){ if(r.toInt32()===1 && !this.out.isNull() && this.kl){ console.log(`[OpenSSL PBKDF2] KEY (${this.kl}B):`); console.log(hexdump(this.out,{length:Math.min(this.kl,64)})); } }  // OpenSSL returns 1 on success
});
```

> **Return conventions differ:** CNG `BCryptDeriveKeyPBKDF2` is NTSTATUS (`===0`),
> OpenSSL `PKCS5_PBKDF2_HMAC` is int (`===1`). Derived key (`args[6]`) is an output —
> read `onLeave`. OWASP 2023 floor is ≥600k iterations; `<10000` is a loud alarm.
> Also hook `BCryptKeyDerivation` / legacy `advapi32!CryptDeriveKey`.

### 2.6 Hunt hardcoded keys & credentials in memory

```js
const m = Process.mainModule;
['-----BEGIN','AKIA','eyJ','SharedAccessKey','password=','BEGIN RSA'].forEach(s => {
  const pat = Array.from(s).map(c=>c.charCodeAt(0).toString(16).padStart(2,'0')).join(' ');
  try { Memory.scanSync(m.base, m.size, pat).forEach(h => { console.log(`[marker '${s}'] @ ${h.address}`); console.log(hexdump(h.address,{length:0x60})); }); } catch(e){}
});
// entropy sweep — max Shannon entropy on a 32-byte window is log2(32)=5.0, so threshold MUST be < 5
function H(p,n){ const f=new Array(256).fill(0); for(let i=0;i<n;i++) f[p.add(i).readU8()]++; let e=0; for(const c of f){ if(c){ const q=c/n; e-=q*Math.log2(q); } } return e; }
Process.enumerateRanges('rw-').filter(r=>r.size<0x200000).forEach(r => {
  for(let off=0; off+32<=r.size; off+=4096){ const a=r.base.add(off); try{ if(H(a,32) > 4.3){ console.log(`[high-entropy 32B] @ ${a}`); console.log(hexdump(a,{length:32})); } }catch(e){} }
});
```

> **Entropy math:** Shannon entropy ≤ `log2(window)` — a 32-byte window maxes at 5.0
> bits/byte, so a `>7.5` test (which only suits ≥256-byte windows) **never fires**;
> `4.3` is ~86% of the ceiling but also matches ciphertext/compressed/ASLR cookies —
> corroborate by watching the bytes flow into a key API (§2.3/2.5). `Memory.scanSync`
> **throws** on unreadable sections — wrap in `try/catch`. Scan **after** login so
> secrets are decrypted/derived. `ptr.toMatchPattern()` finds references to an address.

---

## 3. Auth / license / integrity bypass (authorized)

### 3.1 Force a client-side validator (`isLicensed`/`checkAuth`/`verifyPassword`)

```js
// rizin: izz~license ; find the bool check's RVA (rebase to image base 0 with -B 0)
const FUNC_RVA = 0x12a40;
const m = Process.getModuleByName('app.exe');                 // or the product DLL that owns the check
Interceptor.attach(m.base.add(FUNC_RVA), {                    // ASLR-safe: ALWAYS base + RVA
  onEnter(args){ this.a0 = args[0]; },
  onLeave(retval){ console.log(`[check] orig=${retval.toInt32()} caller=${this.returnAddress}`); retval.replace(1); }  // 1=licensed/authed (0 if fn means isExpired/isTampered)
});
```

> Desktop equivalent of an Android root / iOS jailbreak-detection bypass. Replace `1`
> with `0` when the function returns `is_expired`/`is_tampered`. If the check is
> **inlined** into a conditional jump (no return to patch), `Memory.patchCode` the
> branch instead (flip JE/JNE or NOP the CMP — see [windows-hooking-recipes.md §8] and
> anti-debug A11). Confirm you hit the gate by matching `this.returnAddress` to the xref.

### 3.2 Anti-tamper / Authenticode self-check — `WinVerifyTrust` → 0

```js
const wt = Process.getModuleByName('wintrust.dll');
['WinVerifyTrust','WinVerifyTrustEx'].forEach(fn => { const p = wt.findExportByName(fn); if(!p) return;
  Interceptor.attach(p, { onLeave(retval){ const o=retval.toInt32(); if(o!==0){ console.log(`[${fn}] 0x${(o>>>0).toString(16)} -> 0 caller=${this.returnAddress}`); retval.replace(ptr(0)); } } });
});
```

> Only `0` == trusted (`TRUST_E_SUBJECT_NOT_TRUSTED`=0x800B0004, `TRUST_E_BAD_DIGEST`
> =0x80096010). Many apps **also** re-hash their own `.text` (CRC/SHA vs an embedded
> value) — that never touches wintrust; watch `BCryptHashData`/`CryptHashData` or a
> custom checksum loop and force that comparison (§3.4). Watchdog thread/process? Attach there too.

### 3.3 Password/PIN/key string compares — `lstrcmpW` / `CompareStringW`

```js
const k = Process.getModuleByName('kernelbase.dll');          // lstrcmp* implemented here (kernel32 names them)
['lstrcmpW','lstrcmpiW'].forEach(fn => {
  Interceptor.attach(k.getExportByName(fn), {
    onEnter(args){ this.a=args[0].isNull()?'(null)':args[0].readUtf16String(); this.b=args[1].isNull()?'(null)':args[1].readUtf16String(); },
    onLeave(retval){ console.log(`[${fn}] cmp("${this.a}","${this.b}") -> ${retval.toInt32()}`);   // leaks the EXPECTED secret
      /* force-accept any input: */ // retval.replace(0);     // 0 == EQUAL for lstrcmp*
    }
  });
});
```

> **`lstrcmp` "equal" is 0** — the opposite of a BOOL gate, so `replace(0)` to
> force-accept. `CompareStringW`/`Ex` return `CSTR_EQUAL`=**2** (strings at
> `args[2]`/`args[4]`; force `replace(2)`). Logging alone leaks the expected value —
> no cracking needed. Forcing *every* compare breaks UI logic; gate to the call
> carrying the secret. CRT `wcscmp`/`_wcsicmp` behave like `lstrcmp`.

### 3.4 Hash / memcmp auth — `RtlCompareMemory` / `memcmp`

```js
const nt = Process.getModuleByName('ntdll.dll');
Interceptor.attach(nt.getExportByName('RtlCompareMemory'), {  // returns COUNT of matching bytes; ==len means equal
  onEnter(args){ this.s1=args[0]; this.s2=args[1]; this.len=args[2].toInt32(); },
  onLeave(retval){ if(this.len===0x10 && retval.toInt32()!==this.len){   // 16B => MD4/MD5 hash compare
    console.log(`[RtlCompareMemory] ${this.len}B hash compare:`);
    console.log(hexdump(this.s1,{length:this.len}));           // stored/expected hash -> crack offline
    console.log(hexdump(this.s2,{length:this.len}));           // hash of supplied secret
    retval.replace(this.len);                                  // FORCE full match => auth passes
  } }
});
// memcmp (msvcrt/ucrtbase): 0 == equal -> retval.replace(0)
```

> The SensePost universal-password technique. **Opposite semantics:**
> `RtlCompareMemory` returns a **count** (force `replace(len)`); `memcmp` returns
> **0 on equal** (force `replace(0)`). `RtlEqualMemory` is a **macro, not an export**
> (don't hook it — use `RtlCompareMemory`). Gate on your hash size (0x10/0x14/0x20)
> and caller module — `RtlCompareMemory` is OS-wide hot. Inlined `memcmp` → use §3.1.

### 3.5 Feature flags / license blobs — `RegQueryValueExW`, `CryptUnprotectData`

```js
const adv = Process.getModuleByName('advapi32.dll');
Interceptor.attach(adv.getExportByName('RegQueryValueExW'), {
  onEnter(args){ this.name=args[1].isNull()?'':args[1].readUtf16String(); this.data=args[4]; this.pcb=args[5]; },
  onLeave(retval){ if(retval.toInt32()===0 && /Pro|Premium|Licensed|Debug/i.test(this.name) && !this.data.isNull() && !this.pcb.isNull() && this.pcb.readU32()>=4){
    console.log(`[Reg] ${this.name} -> forcing 1`); this.data.writeU32(1); } }   // REG_DWORD enable
});
// DPAPI-protected license file? dump (and tamper) the decrypted entitlement via CryptUnprotectData (§2.1, read args[6] onLeave).
```

> `lpData` (`args[4]`) is filled only on success (`0`=`ERROR_SUCCESS`); the **first
> call is often a NULL-buffer size probe** — guard `isNull()` and check capacity
> (`args[5].readU32()`) before `writeU32`. Apps also use `RegGetValueW` / read via
> `kernelbase`. Non-DPAPI license files → hook `BCryptDecrypt`/`EVP_DecryptUpdate`
> ([windows-hooking-recipes.md §4.9]).

---

## 4. Sensitive-data flow

### 4.1 Clipboard — `SetClipboardData` / `GetClipboardData`

```js
const u32 = Process.getModuleByName('user32.dll'), k32 = Process.getModuleByName('kernel32.dll');
const GlobalLock = new NativeFunction(k32.getExportByName('GlobalLock'), 'pointer', ['pointer']);
const GlobalUnlock = new NativeFunction(k32.getExportByName('GlobalUnlock'), 'int', ['pointer']);
function dumpClip(tag, fmt, hMem){ if(hMem.isNull()) return; const p=GlobalLock(hMem); if(p.isNull()) return;
  const s = fmt===13 ? p.readUtf16String() : (fmt===1 ? p.readAnsiString() : null);   // CF_UNICODETEXT=13, CF_TEXT=1
  console.log(`[${tag}] fmt=${fmt} `+(s!==null?`"${s}"`:'')); if(s===null) console.log(hexdump(p,{length:0x40})); GlobalUnlock(hMem); }
Interceptor.attach(u32.getExportByName('SetClipboardData'), { onEnter(a){ this.fmt=a[0].toInt32(); this.h=a[1]; }, onLeave(){ dumpClip('SetClipboardData', this.fmt, this.h); console.log('  caller:', DebugSymbol.fromAddress(this.returnAddress)); } });
Interceptor.attach(u32.getExportByName('GetClipboardData'), { onEnter(a){ this.fmt=a[0].toInt32(); }, onLeave(r){ dumpClip('GetClipboardData', this.fmt, r); } });
```

> `hMem`/retval is an **HGLOBAL handle, not a pointer** — `GlobalLock` it first.
> Branch the read on `uFormat` (wide vs ANSI). `hMem` can be NULL on `SetClipboardData`
> (delayed rendering). OLE drag-drop / Clipboard History API bypass these.

### 4.2 Command-line & environment secrets

```js
const k32 = Process.getModuleByName('kernel32.dll');
const SECRET = /(pass|pwd|secret|token|key|cred|auth|bearer|conn)/i;
Interceptor.attach(k32.getExportByName('GetCommandLineW'), { onLeave(r){ const cl=r.isNull()?'':r.readUtf16String(); if(SECRET.test(cl)) console.log('[CmdLine!] '+cl); } });
Interceptor.attach(k32.getExportByName('GetEnvironmentVariableW'), {
  onEnter(a){ this.name=a[0].isNull()?'':a[0].readUtf16String(); this.buf=a[1]; this.nSize=a[2].toInt32(); },
  onLeave(r){ const n=r.toInt32(); if(n===0 || this.buf.isNull() || n>=this.nSize) return;   // n>=nSize => sizing probe, buffer UNDEFINED
    console.log(`[GetEnv] ${this.name} = ${this.buf.readUtf16String()}`+(SECRET.test(this.name)?'  <-- SENSITIVE':'')); }
});
```

> Secrets in argv/env are visible to any local user (Task Manager/ProcMon).
> `GetCommandLineW` is called **once early** → spawn with `-f`. `GetEnvironmentVariableW`
> is called **twice** (size probe then fetch) — on the probe `retval` = required size
> and the buffer is **undefined**; the `n>=nSize` guard drops it. Also catches secrets
> on child `CreateProcessW` command lines ([windows-hooking-recipes.md §4.6]).

### 4.3 Named-pipe IPC — `NtWriteFile` / `NtReadFile`

```js
const nt = Process.getModuleByName('ntdll.dll');              // catches sync + overlapped, server + client
Interceptor.attach(nt.getExportByName('NtWriteFile'), {       // (h,Event,Apc,ApcCtx,Iosb,Buffer=a5,Length=a6,ByteOffset,Key)
  onEnter(a){ const n=a[6].toInt32(); if(n>0 && !a[5].isNull()){ console.log(`[NtWriteFile] ${n}b ->`); console.log(hexdump(a[5],{length:Math.min(n,0x100)})); } }
});
Interceptor.attach(nt.getExportByName('NtReadFile'), {
  onEnter(a){ this.buf=a[5]; this.iosb=a[4]; },
  onLeave(r){ if(r.toInt32()===0x103) return;                 // STATUS_PENDING: async, not filled yet
    if(this.buf.isNull()||this.iosb.isNull()) return;
    const n=this.iosb.add(Process.pointerSize).readUInt();    // IO_STATUS_BLOCK.Information = bytes read (NOT retval)
    if(n>0){ console.log(`[NtReadFile] ${n}b <-`); console.log(hexdump(this.buf,{length:Math.min(n,0x100)})); } }
});
```

> `NtReadFile` byte count is `IO_STATUS_BLOCK.Information` (second pointer-sized field),
> **not** the retval; bail on `STATUS_PENDING` (0x103). This hooks **all** file I/O —
> build a handle→name map from `CreateNamedPipeW`/`CreateFileW` (`\\.\pipe\…`) and
> filter, or it's very noisy. Async pipes also need `GetOverlappedResult` / completion.

### 4.4 JWT / token / PII detector (reusable scanner)

```js
const RULES = [['JWT',/eyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}/],['Bearer',/Bearer\s+[A-Za-z0-9._~+\/-]{12,}=*/i],
  ['Email',/[A-Za-z0-9._%+-]{1,}@[A-Za-z0-9.-]{1,}\.[A-Za-z]{2,}/],['AWSKey',/AKIA[0-9A-Z]{16}/]];
function scanSecrets(tag, ptr, len, ra){ if(ptr.isNull()||len<=0) return;
  let s; try{ s=ptr.readByteArray(Math.min(len,0x2000)); }catch(e){ return; } if(s===null) return;
  const txt = String.fromCharCode.apply(null, new Uint8Array(s)).replace(/\0/g,'');   // \0 strip => also catches ASCII inside UTF-16
  for(const [name,re] of RULES){ const m=txt.match(re); if(m){ console.log(`[LEAK ${name}] in ${tag}: ${m[0].slice(0,80)}`); if(ra) console.log('  at '+DebugSymbol.fromAddress(ra)); } }
}
// call from any buffer hook: scanSecrets('NtWriteFile', a[5], a[6].toInt32(), this.returnAddress);
```

> Drop into clipboard / pipe / file-write / TLS-plaintext hooks. The NUL-strip makes
> one pass catch both ANSI and UTF-16 payloads. Cap the scan (`0x2000`) — regexing MB
> buffers in a hot hook lags the target. `DebugSymbol` gives module+offset without a PDB.

### 4.5 Local RPC / COM — `NdrClientCall3`, `CoCreateInstance`

```js
const rpc = Process.getModuleByName('rpcrt4.dll');
Interceptor.attach(rpc.getExportByName('NdrClientCall3'), {   // (pProxyInfo, nProcNum=a1, pReturnValue, ...marshalled)
  onEnter(a){ console.log(`[NdrClientCall3] procNum=${a[1].toInt32()} caller=${DebugSymbol.fromAddress(this.returnAddress)}`); }
});
Interceptor.attach(Process.getModuleByName('combase.dll').getExportByName('CoCreateInstance'), { onEnter(a){ console.log('[CoCreateInstance] CLSID @', a[0]); } });
```

> NDR args are a packed format-driven blob — capture **procNum + interface UUID**, then
> dump the assembled payload on the transport (Nt*File §4.3 / Schannel §1.1 / ALPC
> `ntdll!NtAlpcSendWaitReceivePort`). In-proc COM never reaches rpcrt4; only
> out-of-proc/cross-apartment does. Legacy stubs use `NdrClientCall2`.

---

## 5. Attack surface mapping + fuzzing

### 5.1 Map input handling — `frida-trace` + `frida-discover`

```powershell
# Which app functions fire while you replay ONE sample? (open the file / send the packet, then Ctrl-C)
frida-trace -p <pid> -i "appcore.dll!*Parse*" -i "appcore.dll!*Decode*" -i "ws2_32.dll!recv" --decorate
# Find UNEXPORTED hot functions (exercise the input path WHILE it runs):
frida-discover -p <pid>                 # prints appcore.dll!0xNNNN + hit-count
# Trace a discovered internal function by offset:
frida-trace -p <pid> -a "appcore.dll!0x4793c"
```

> `-i`/`-x` are **function** include/exclude (`[MODULE!]FUNC` globs, applied in
> command-line order); `-I`/`-X` are **module**-level — don't pass a function glob to
> `-X`. Scope to the app DLL (whole-CRT traces are noise). `frida-discover` must run
> *while* you exercise input. Offsets are module-relative — re-resolve against the base.

### 5.2 Mutate live input in-process

```js
const ws2 = Process.getModuleByName('ws2_32.dll');           // recv fills args[1] on RETURN; retval = bytes
Interceptor.attach(ws2.getExportByName('recv'), {
  onEnter(args){ this.buf=args[1]; },
  onLeave(retval){ const n=retval.toInt32(); if(n<=0) return;
    for(let i=0;i<Math.min(n,8);i++){ const off=(Math.random()*n)|0; this.buf.add(off).writeU8((Math.random()*256)|0); }  // havoc WITHIN n bytes
    // or force a boundary: this.buf.add(4).writeU32(0xffffffff);
  }
});
```

> Write only **within the bytes actually read** (retval / `*bytesRead`) — overrunning
> corrupts the app heap. Mutate read-on-leave buffers in `onLeave`. `WinHttpReadData`
> in async mode isn't filled on return (mutate the completion path instead). Perturbs
> one run — for systematic coverage use §5.3.

### 5.3 In-process fuzz harness — `NativeFunction` loop

```js
const m = Process.getModuleByName('appcore.dll');
const parse = new NativeFunction(m.getExportByName('ParseMessage'), 'int', ['pointer','size_t'], (Process.arch==='ia32')?{abi:'stdcall'}:undefined);
const buf = Memory.alloc(0x4000);                            // keep ref alive (GC frees it otherwise)
const seed = [0x4d,0x53,0x47,0x01,0,0,0,0];
function mutate(b){ const o=b.slice(); for(let i=0;i<4;i++) o[(Math.random()*o.length)|0]=(Math.random()*256)|0; return o; }
for(let i=0;i<100000;i++){ const data=mutate(seed); buf.writeByteArray(data);
  try{ parse(buf, data.length); } catch(e){ console.log('[CRASH] iter',i,e.message); console.log(hexdump(buf,{length:Math.min(data.length,0x40)})); }
}
```

> `try/catch` catches access violations (Frida converts them to JS exceptions), but a
> corrupted heap can crash **later** outside the catch — pair with an out-of-process
> harness (frida-fuzzer/fpicker) for stability. Stateful parsers drift → reset between
> iterations. **x86** C++ methods need `{abi:'thiscall'}` + `this` as arg0; x64 = default.
> Feed a real mutated corpus (radamsa/AFL), not the toy `mutate()`.

### 5.4 Basic-block coverage → DRcov (Lighthouse / fuzzer feedback)

```js
const mod = Process.getModuleByName('appcore.dll');
const lo = mod.base, hi = mod.base.add(mod.size), blocks = [];
Process.enumerateModules().forEach(m => { if(m.name!==mod.name) Stalker.exclude({base:m.base, size:m.size}); });
Stalker.trustThreshold = 0;
const tid = Process.getCurrentThreadId();
Stalker.follow(tid, { events:{ compile:true },
  onReceive(events){ for(const [start,end] of Stalker.parse(events,{stringify:false,annotate:false})){
    if(start.compare(lo)>=0 && start.compare(hi)<0){ blocks.push([start.sub(lo).toInt32(), end.sub(start).toInt32()]); }   // toInt32 — NOT toUInt32
  } }
});
// ... run one input ... then:
Stalker.unfollow(tid); Stalker.flush();
console.log('[cov] in-module blocks:', blocks.length);
// pack each [u32 off][u16 size][u16 modId=0] into one ArrayBuffer; emit "DRCOV VERSION: 2" file (see lighthouse frida-drcov.py)
```

> Compute offsets with `start.sub(base).toInt32()` (matches lighthouse) — **never
> `toUInt32` (it doesn't exist)**. `compile` fires **once per block** for the whole
> session — to get fresh per-input coverage, `unfollow`/refollow or `Stalker.invalidate`
> with `trustThreshold=0`. Stalker is heavy: **always `Stalker.exclude` every other
> module** and scope to one thread. Match the DRcov header (`DRCOV VERSION: 2` /
> `DRCOV FLAVOR: frida` / `Columns: id, base, end, entry, checksum, timestamp, path`)
> byte-for-byte or Lighthouse rejects it.

### 5.5 Rank targets + dump magic-value compares (cmplog-lite)

```js
const mod = Process.getModuleByName('appcore.dll'), lo = mod.base;
Process.enumerateModules().forEach(m => { if(m.name!==mod.name) Stalker.exclude({base:m.base, size:m.size}); });
Stalker.trustThreshold = 0;
const cmpAddr = mod.base.add(0x4a1c);                         // a magic-number CMP found statically
Stalker.follow(Process.getCurrentThreadId(), { events:{ call:true },
  onCallSummary(summary){ Object.entries(summary).sort((a,b)=>b[1]-a[1]).slice(0,10).forEach(([a,c]) => console.log(c, ptr(a).sub(lo))); },
  transform(it){ let i=it.next(); do { if(i.address.equals(cmpAddr)) it.putCallout(cpu => console.log('[cmp] rax=',cpu.rax,'rdx=',cpu.rdx)); it.keep(); } while((i=it.next())!==null); }
});
```

> Provide **only one** of `onReceive`/`onCallSummary`. `putCallout` gets a
> `GumCpuContext` — x64 `cpu.rax/rdx/...`, x86 `cpu.eax/...` (wrong width = garbage).
> The callout only runs if the block is (re)compiled — `trustThreshold=0` or
> `Stalker.invalidate(cmpAddr)` when attaching late. Modifying regs in the callout
> **propagates** to the app (force a branch, but you can corrupt state).

---

## Sources & provenance

Synthesized from a verified research pass over Microsoft Learn (Win32/SSPI/DPAPI/
wincrypt/wintrust/wincred APIs), OpenSSL/NSS docs, the bundled
[frida-docs](frida-docs/), and public Frida appsec write-ups (httptoolkit cert-pinning,
SensePost universal-password, lighthouse `frida-drcov.py`, frida-fuzzer, NCC
DatajackProxy, Synacktiv named-pipe hooking). All Windows export↔DLL pairings and
argument indices were checked against live System32 DLLs (`rz-bin -qE`) and MS docs;
all Frida API usage against [frida-docs/javascript-api.md](frida-docs/javascript-api.md)
(Frida 17 — instance `getExportByName`, `toInt32` not `toUInt32`, `retval.replace(ptr(N))`).
Struct offsets (`CREDENTIALW`, `CERT_CHAIN_POLICY_STATUS`, `DATA_BLOB`, `SecBuffer`,
`IO_STATUS_BLOCK`) are **x64** — verify against your target build.
