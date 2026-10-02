# Documentation

## Guides

- [Contributing and repository layout](../CONTRIBUTING.md)
- [Runtime releases and distribution](guides/releases.md)
- [Build the Rizin runtime](guides/rizin-build.md)
- [Use the portable Rizin runtime](guides/rizin-runtime.md)

Plugin setup and agent workflows belong to each plugin's own directory; start from the [plugin catalog](../README.md#plugins).

## Reference

- [Rizin third-party components and licenses](reference/rizin-third-party.md)
- [Hyper-V feature inventory](../plugins/hyper-v-control/docs/extraction.md)

## Validation records

These are dated or versioned records of what was tested, including environment details and known limits. They are historical evidence, not installation guides.

| Component | Record |
| --- | --- |
| Rizin | [Runtime validation](validation/rizin/runtime-v0.3.1/report.md) |
| Capstone Binary Patching | [Native patching experiments](validation/capstone-binary-patching/plugin-v0.1.0/report.md) |
| Frida Use | [Windows instrumentation experiments and evidence](validation/frida-use/2026-10-01/report.md) |
| Hyper-V Control | [VM, console, and debugger validation](validation/hyper-v-control/runtime-v0.1.0/report.md) |

## Where new documents belong

- `guides/`: maintained task procedures.
- `reference/`: component facts, formats, and notices.
- `validation/<component>/<version-or-date>/`: one report and its retained evidence for each run.

Keep this directory root limited to this index. Link to existing plugin documentation rather than duplicating it. Add new validation runs alongside previous records; update links when moving documents, and keep generated binaries and large captures under ignored `build/`.
