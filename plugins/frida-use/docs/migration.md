# Import into Frida Use

Imported on 2026-10-01 from the user-provided project at commit
`180b5c128894a506493fef40ef72849a6835a24e` (clean working tree at inspection).

Every non-Git source file is preserved byte-for-byte. [import-manifest.json](import-manifest.json)
maps original paths to their bundled locations and records SHA-256 digests.
The import excludes only Git metadata; no submodule, symlink, junction, source
checkout, absolute runtime path, or old repository download is required.

| Original material | Bundled destination |
| --- | --- |
| LICENSE, assets | Plugin LICENSE and assets |
| README, skill entrypoint, both plugin manifests | `docs/migration/original-*` (historical, not discovered as a skill/plugin) |
| All synthesized reference sheets | `skills/frida-use/references/legacy/` |
| Frida website documentation snapshots | `skills/frida-use/references/legacy/frida-docs/` |
| Check Point Anti-Debug-DB snapshots | `skills/frida-use/references/legacy/anti-debug-db/` |

Current entrypoints, manifests and focused reference guides were rewritten under
the new identity. Archived examples deliberately retain original bytes, old names,
old links, fixed addresses, and API mistakes so the import is auditable. Consult
the maintained guides before adapting any historical snippet. Relative links in
historical files may refer to the original project/site layout.

Significant corrections include file offset vs RVA, DLL forwards and load timing,
Frida 17 API removals, x86/x64 host-vs-target architecture, target resume ordering,
overlapped I/O, pointer lifetimes, Windows return conventions, and separating
agent debugging from native CPU debugging. Original source deletion cannot affect
this plugin. The repository's existing Rizin build submodules are unrelated.
