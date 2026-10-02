# Validation: Rizin bundle 0.3.1

Verified on October 1, 2026 on Windows 11 x64. The build uses Rizin 0.9.1, the pinned current plugin revisions, YARA 4.5.8, and VS 2026 toolchain 14.51.36231.

## Results

- The build script completed end to end, including source revision checks, compatibility patches, dependency checksum verification, compilation, and runtime assembly.
- The final archive was freshly extracted to a path containing spaces and Chinese characters. Inputs were in a separate Unicode path; test outputs were also exercised in a Unicode path.
- All 14 runtime checks passed with a minimal PATH and without SLEIGHHOME or Rizin configuration overrides. The final verification process was not running as administrator.
- All 58 shipped PE executables/DLLs are AMD64. Normal and delay-load imports resolve to bundled DLLs or Windows system/API-set libraries. The VC++ runtime is present beside the executables.
- RetDec, Ghidra, and jsdec recovered the fixture's multiplication by 3 and addition of 7. All three JSON output commands returned valid nonempty JSON.
- Ghidra found its SLEIGH data inside the relocated runtime. YARA's PE/string and OpenSSL SHA-256 rules both matched.
- The bundled sigdb was discoverable. A FLIRT signature was generated from the fixture and applied successfully.
- A real minidump of the benign fixture's own process exposed 180 captured maps. Ghidra and jsdec recovered the exported function from captured memory. Raw-region testing confirmed an explicit x64 virtual base and disassembly.
- Installer tests passed: valid checksum install and execution, preservation of an existing install, and rejection of a corrupt checksum before installation. The regression test emulates the real Invoke-RestMethod array response and includes an unrelated marketplace release. The corrected installer was also exercised against the actual public GitHub release API and download.
- Codex CLI parsed the marketplace and reported rizin-windows-re@my-plugin-marketplace version 0.3.1 as available. The portable manifest passed its published JSON Schema, compatibility metadata agrees, and the skill passed skill-creator validation.

Machine-readable evidence is included as verification.json in the release. Its archive digest identifies the exact tested runtime. No minidump or process-memory content is published.

## Fixes found by testing

1. Adapted Ghidra's print API and portable SLEIGH path lookup to Rizin 0.9.1.
2. Fixed RetDec Windows linkage and runtime-relative support data lookup.
3. Left unknown RetDec return types unspecified instead of forcing void, which had erased pure calculations.
4. Updated jsdec's removed ec* command to ecj palette data.
5. Embedded a UTF-8/asInvoker manifest into every CLI: upstream tools could not load plugins from Unicode paths without it. This changes only each process's code page, requiring Windows 10 version 1903 or later; no system locale setting changes.
6. Excluded YARA's upstream fuzz corpus from source checkout after ESET detected Android test samples. These samples are not used for compilation and are absent from the runtime.

## Scope and known limitations

This is relocation and dependency validation on the current Windows host, not a fresh VM test. Windows 10 was not separately exercised. Application manifests and dependency scanning support the stated Windows 10 (1903+)/11 x64 target, but cannot cover every Windows configuration.

Rizin 0.9.1 reports unsupported minidump streams 21/22 and PE certificate-length diagnostics while reading this Windows 11 dump. The verifier records those specific diagnostics; mapped memory, exports, and tested code recovery still pass. Do not infer complete minidump-stream or Authenticode recovery from this test. Kernel DMP64 and ELF-core workflows are documented from upstream capabilities but were not fixture-tested.

RetDec's upstream SDK revision is unchanged and retains its legacy OpenSSL 1.1.1w dependency; it is not claimed to use the current OpenSSL ABI. YARA uses OpenSSL 3.5.9 LTS. Decompiled types remain inferred and should be checked against disassembly.

Version 0.3.1 fixes release selection when Invoke-RestMethod returns the GitHub JSON array as a single pipeline object. Runtime native code is unchanged from 0.3.0; the helper, skill metadata, and bundle metadata are updated.
