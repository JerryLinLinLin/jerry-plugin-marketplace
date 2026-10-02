# Research sources and design rationale

Research checked on 2026-10-01. The references below retain original authors, official
documentation and source repositories, with brief descriptions of the ideas adopted here.
Community scripts were not copied. Documentation, examples and local experiments are separate
forms of evidence; linking a script does not authorize executing it. Chinese source titles
are translated into English below.

## English sources: engines, patching and diffing

| Primary source | Relevance and adopted approach |
| --- | --- |
| [Capstone repository](https://github.com/capstone-engine/capstone) and [Python API](https://www.capstone-engine.org/lang_python.html) | Explicit architecture/mode, address, instruction length and details. The C engine has Python bindings; it neither generates patches nor decompiles. Architecture counts in old tutorials are not the current support matrix. |
| [Capstone SKIPDATA](https://www.capstone-engine.org/skipdata.html) | Decoding normally stops at unknown bytes. Skipdata is exploratory and does not establish code boundaries; the helper must consume the full code interval. |
| [Keystone Python sample](https://github.com/keystone-engine/keystone/blob/master/bindings/python/sample.py) | Assemble bytes for the actual target address and mode, then inspect them with Capstone. Preserve the real VA when generating replacements. |
| [Keypatch original project](https://github.com/keystone-engine/keypatch) | A real IDA patching tool with assembly editing, range filling, NOP padding, original-instruction records and undo. Its patch lifecycle informs this plugin without requiring IDA. Historical IDA/Python 2 installation steps are not current setup instructions. |
| [LIEF](https://lief.re/), [PE documentation](https://lief.re/doc/latest/formats/pe/index.html), [PE patch implementation](https://github.com/lief-project/LIEF/blob/main/src/PE/Binary.cpp) and [ELF implementation](https://github.com/lief-project/LIEF/blob/main/src/ELF/Binary.cpp) | Executable parsing/editing requires distinct VA, RVA and file bounds. Equal-length edits use an exact byte copy; structural rebuilding is a separate workflow. |
| [LIEF 0.16 announcement](https://lief.re/blog/2024-12-10-lief-0-16-0/) | Explains LLVM-based assembly/disassembly and how it differs from Capstone/Keystone. Some capabilities belong to Extended and cannot be assumed available in the PyPI Core package. |
| [macho-diff by NohamR](https://github.com/NohamR/macho-diff) and [author's mirrored README](https://git.noh.am/noham/macho-diff/src/branch/main/README.md) | Uses LIEF and Capstone to compare function hashes, instructions and operands, and generate pattern patches. This plugin additionally binds patches to whole-file hashes. |
| [QBinDiff by Quarkslab](https://github.com/quarkslab/qbindiff) and [author introduction](https://blog.quarkslab.com/qbindiff-a-modular-diffing-toolkit.html) | Function/call-graph matching is a separate analysis layer using BinExport/Quokka exports; relevant backends use Capstone. Code movement across versions cannot be handled solely by comparing file offsets. |
| [Patcherex2](https://github.com/purseclab/Patcherex2) and [InsertInstructionPatch example](https://purseclab.github.io/Patcherex2/examples/insert_instruction_patch/) | Demonstrates modifying a native program and checking execution. Supports insertion, replacement and function edits with a target matrix and additional dependencies; suitable when equal-length editing is insufficient. |
| [Patcherex2 Thumb relocation issue #38](https://github.com/purseclab/Patcherex2/issues/38) | Misidentified ARM/Thumb state can break relocation. This motivates explicit execution-state inputs rather than guessing. The issue is a specific report, not a claim that every release has the defect. |
| [Unicorn official tutorial](https://www.unicorn-engine.org/docs/tutorial.html) | Controlled CPU execution with explicit registers and memory helps validate instruction behavior, but does not replace an OS loader or complete malware isolation environment. |
| [xdelta](https://github.com/jmacd/xdelta) and [bsdiff](https://github.com/mendsley/bsdiff) | Encode and apply file deltas for distributing already validated outputs; distinct from semantic diffing. |
| [uv scripts](https://docs.astral.sh/uv/guides/scripts/) and [uv installation](https://docs.astral.sh/uv/getting-started/installation/) | PEP 723 metadata and script locks provide consistent installation, execution and dependency resolution. |

## Chinese primary sources

| Original source | Observation and limits |
| --- | --- |
| [xiusi: An Introduction to the Keystone Framework, Kanxue](https://bbs.kanxue.com/thread-289573.htm) | Separates disassembly, assembly and emulation; demonstrates address-dependent assembly and emulated edits, and identifies the correct keystone-engine package name. Adopted the combined workflow with installation converted to uv. Current API behavior is checked against pinned versions rather than treating every tutorial statement as authoritative. |
| [bbqz007: My Reverse-Engineering Toolkit, libKTLtdx](https://www.cnblogs.com/bbqzsl/p/18786157) | The author integrates Capstone/Keystone and discusses dynamic patches and memory protection. Supports the distinction between file edits and process edits; this CLI does not write process memory. |
| [Reproducing the 2023 Tencent Game Security Competition Android Final, Kanxue](https://bbs.kanxue.com/thread-287915.htm) | Search-index excerpts show Capstone, Keystone and Unidbg used to track branches, generate patch lists and write copies. Direct access triggered a verification page. Its code was not executed and its API details are not treated as verified. Retained as a deobfuscation example to investigate. |
| [An xxmain.so Analysis: From Junk-Code Removal to Algorithm Recovery, Kanxue](https://bbs.kanxue.com/thread-283569.htm) | The indexed summary describes Capstone instruction analysis and patching recovered branch targets. Full-page access required verification. Retained as a practice reference, not evidence that every branch can be treated as constant. |

Searches combined Capstone, binary patching, binary diff, Keystone, LIEF, malware analysis,
deobfuscation and Kanxue in English and Chinese, then followed results to original READMEs,
author articles and source code. Aggregated reposts and software-activation guides were not
used as default patching workflows.

During research, the old Keystone `keypatch` website path redirected to an unrelated site.
The references therefore use the original GitHub project and PyPI rather than that redirect
for downloads. Current LIEF documentation may be newer than the pinned package, so API use
also requires checking the installed version and implementation.

## Versions and execution evidence

- [Capstone 5.0.9](https://pypi.org/project/capstone/5.0.9/) and [LIEF 1.0.0](https://pypi.org/project/lief/1.0.0/): pinned base dependencies.
- [Keystone-engine 0.9.2](https://pypi.org/project/keystone-engine/0.9.2/): optional assembler.
- [Unicorn 2.1.4](https://pypi.org/project/unicorn/2.1.4/): CPU emulator used in experiments.

Consult the plugin's `experiments/` directory and repository report
`docs/validation-capstone-binary-patching-v0.1.0.md` for local execution results. Listing an
optional tool here does not mean it was installed or tested on this host.
