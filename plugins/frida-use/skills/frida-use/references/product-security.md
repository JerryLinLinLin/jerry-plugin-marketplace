# Desktop application security and data-flow analysis

Apply instrumentation to the requested application and test input. Define the
boundary being investigated (TLS, parser, secret lifetime, client/server decision)
before selecting hooks. A client-side modification can demonstrate local behavior;
it does not establish that the server accepted a privilege or authentication change.

## TLS and protocol boundaries

Choose hooks based on the actual stack: Schannel/SSPI, WinHTTP/WinINet, a bundled
OpenSSL/BoringSSL/NSS build, .NET or a custom library. API forwards and runtime
library versions make fixed DLL ownership assumptions brittle.

For Schannel, `EncryptMessage` receives plaintext in its SecBufferDesc before
sealing. `DecryptMessage` modifies buffers in place; inspect them only after a
status that the documented API contract permits. Incomplete-message, renegotiation,
context-expired and failure statuses need separate handling. Do not assume every
buffer after every return contains plaintext.

`SecBufferDesc` has 32-bit version/count and a pointer at offset 8. `SecBuffer`
contains two 32-bit fields and a pointer, so stride is `8 + Process.pointerSize`
(12 on x86, 16 on x64). Mask attribute bits when comparing `BufferType`, validate
count/length, cap each read, and select data buffers. A fixed 16-byte stride is
wrong for x86. Consult the exact structure and status declarations.

Sources: [DecryptMessage (Schannel)](https://learn.microsoft.com/en-us/windows/win32/secauthn/decryptmessage--schannel),
[SecBuffer](https://learn.microsoft.com/en-us/windows/win32/api/sspi/ns-sspi-secbuffer),
[SecBufferDesc](https://learn.microsoft.com/en-us/windows/win32/api/sspi/ns-sspi-secbufferdesc).

For a bundled crypto library, verify its exports, version and prototype. A hook
on a Windows crypto API cannot observe a library that never calls it. Streaming
encryption may emit partial blocks; the init/update/final state and buffer lengths
matter. Limit plaintext capture to the data needed for the requested test.

For certificate validation experiments, record the normal handshake and exact
error first. A supported test trust store or explicit test endpoint often gives
a more interpretable comparison. If the task requires a temporary validation
patch, preserve all output/result semantics and scope it to that test. There is
no universal one-return-value patch for every Schannel, WinVerifyTrust or pinning
path. Keep trust configuration changes separate from runtime observations.

## Crypto and secrets

Inspect the narrow point where test data enters/leaves an operation. DPAPI
DATA_BLOB uses a 32-bit byte count plus an aligned pointer (pointer at 4 on x86,
8 on x64); follow the called API's allocation/free contract. Treat encrypted
buffers, opaque key handles and raw key material as different data types.

For an audit, prefer synthetic credentials and bounded captures. Check algorithm
selection, parameters, IV reuse across controlled runs, and memory lifetime;
a value observed once does not prove it is hardcoded. Preserve API status and
actual output sizes. Store sensitive case evidence in the user's analysis output,
never in the plugin package.

Source: [CryptUnprotectData](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptunprotectdata).

## Input mapping and temporary patches

Use a selected parser boundary and known inputs to map call chains. Correlate
caller module/RVA, lengths and status with the produced behavior. Stalker can
compare selected-thread coverage between inputs, but instrumentation overhead
and incomplete thread coverage must be accounted for.

To test error handling, alter one controlled argument or return with a matching
output/error state, then restore it. Changing a client UI/boolean alone says
little about server enforcement. Internal C++/managed methods may be inlined,
JIT-recompiled or have signatures unlike a C export. Use native prototypes,
symbols or a supported managed-runtime bridge rather than guessing from an RVA.

See [runtime patching](patching.md) and [Windows debugging](windows-debugging.md).
