# Third-party components

This bundle is an aggregation. MIT licensing for the repository/skill does not replace component licenses. License texts are under licenses/ in the runtime. bundle-manifest.json records upstream commits and download hashes; the release tag includes the corresponding Windows patches and build scripts.

| Component | Source and license |
| --- | --- |
| Rizin 0.9.1 | [rizinorg/rizin](https://github.com/rizinorg/rizin/tree/v0.9.1), LGPL-3.0 with component-specific notices in LICENSES/. Shipped as replaceable shared libraries. |
| rz-ghidra | [rizinorg/rz-ghidra](https://github.com/rizinorg/rz-ghidra), LGPL-3.0. |
| Ghidra decompiler/language data | [rizinorg/ghidra](https://github.com/rizinorg/ghidra/tree/3e5d774389b3c4ff1c089ddf822f071c10f548a8), Apache-2.0 and included LICENSE/NOTICE. |
| pugixml | [zeux/pugixml](https://github.com/zeux/pugixml), MIT. |
| jsdec | [rizinorg/jsdec](https://github.com/rizinorg/jsdec), BSD-3-Clause and bundled notices. |
| QuickJS-NG 0.8.0 | [quickjs-ng/quickjs](https://github.com/quickjs-ng/quickjs/tree/v0.8.0), MIT. |
| rz-retdec | [rizinorg/rz-retdec](https://github.com/rizinorg/rz-retdec), LGPL-3.0-only. |
| RetDec 5.0 SDK/data | [rizinorg/retdec](https://github.com/rizinorg/retdec/tree/8272d0355794f8b8a63d8611b1dea50b4f2d87c3), MIT plus LLVM, YARA, Capstone, and other notices in LICENSE-THIRD-PARTY. Reused from the SHA-256-locked previous bundle; its upstream revision is unchanged. [Historical SDK build patches](https://github.com/JerryLinLinLin/rz-retdec/tree/0b065b6a98e526ddcd6f43ede6d5aa645a50206d). |
| rz-libyara | [rizinorg/rz-libyara](https://github.com/rizinorg/rz-libyara), included LICENSES/. |
| YARA 4.5.8 | [VirusTotal/yara](https://github.com/VirusTotal/yara/tree/v4.5.8), BSD-3-Clause and included notices. Only library sources are used; fuzz corpora are excluded. |
| OpenSSL 3.5.9 and 1.1.1w | [FireDaemon distributions](https://kb.firedaemon.com/support/solutions/articles/4000121705-openssl-binary-distributions-for-microsoft-windows), license texts included. 3.5.9 serves YARA; RetDec requires the older 1.1.1w ABI. |
| zlib 1.3.2 | [madler/zlib](https://github.com/madler/zlib/tree/v1.3.2), zlib license; statically linked into Ghidra. |
| sigdb | [rizinorg/sigdb](https://github.com/rizinorg/sigdb), LGPL-3.0; source generation at [sigdb-source](https://github.com/rizinorg/sigdb-source). |
| Visual C++ runtime | Microsoft app-local redistributable DLLs from VS 2026 Build Tools VC/Redist/MSVC/.../x64/Microsoft.VC145.CRT. Governed by Microsoft terms; see [redistributing Visual C++ files](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files). |

Rebuild modified LGPL components using the pinned submodules, patches, and build guide in the release's repository tag. Rizin shared libraries are from the official Windows x64 release. CLI executables receive the included UTF-8/asInvoker application manifest so Unicode paths work without system locale changes; their source code is unchanged.
