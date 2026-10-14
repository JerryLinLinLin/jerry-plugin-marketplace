# Frida Use import archive

This directory archives the byte-for-byte import of the user-provided project that
Frida Use supersedes (`frida-windows-skills`, which shipped the `frida-windows-re`
skill). Every non-Git source file at commit
`180b5c128894a506493fef40ef72849a6835a24e` (clean working tree at inspection) was
imported on 2026-10-01. On 2026-10-13 the imported documentation and original
manifests were relocated here, out of the installed plugin, so that the plugin
ships only maintained material. Nothing here is installed or maintained as current
instructions.

## Contents

| Entry | What it is |
| --- | --- |
| [import-manifest.json](import-manifest.json) | Maps each of the 31 imported files from its original path to its current location, relative to the repository root, and records its SHA-256 digest |
| [original-readme.md](original-readme.md) | The project's `README.md` |
| [original-skill.md](original-skill.md) | The project's skill entrypoint, `skills/frida-windows-re/SKILL.md` |
| [original-plugin.json](original-plugin.json) | The project's root `plugin.json` manifest |
| [original-codex-plugin.json](original-codex-plugin.json) | The project's Codex manifest, `.codex-plugin/plugin.json` |
| [references/](references/README.md) | The project's synthesized reference sheets, from `skills/frida-windows-re/references/` |
| `references/frida-docs/` | Frida website documentation snapshots |
| `references/anti-debug-db/` | Check Point Anti-Debug-DB snapshots |

The recorded digests cover the original bytes, which use CRLF line endings. This
README and `references/README.md` are archive notes, not imported files.

## What the plugin still ships

The installed plugin keeps two imported files, the original
[LICENSE](../../../plugins/frida-use/LICENSE) and
[assets/icon.svg](../../../plugins/frida-use/assets/icon.svg), together with the
upstream license notices for the
[Frida website](../../../plugins/frida-use/licenses/frida-website-MIT.txt) and
[Anti-Debug-DB](../../../plugins/frida-use/licenses/anti-debug-db-MIT.txt). Those
notices also apply to the snapshots archived here; the plugin's
[third-party notices](../../../plugins/frida-use/THIRD-PARTY-NOTICES.md) record
their origin and retrieval revisions.

## Reading archived files

Archived files keep their original bytes, names, old links (which may not
resolve), fixed addresses, and API mistakes, so the import stays auditable.
Consult the maintained [Frida Use skill](../../../plugins/frida-use/skills/frida-use/SKILL.md)
and [plugin README](../../../plugins/frida-use/README.md) first, and recheck any
historical snippet before adapting it.

The maintained entrypoint, manifests, and focused reference guides were rewritten
under the new identity. Significant corrections include file offset vs RVA, DLL
forwards and load timing, Frida 17 API removals, x86/x64 host-vs-target
architecture, target resume ordering, overlapped I/O, pointer lifetimes, Windows
return conventions, and separating agent debugging from native CPU debugging.
Deleting the original project cannot affect the plugin or this archive.

The [validation record](../../validation/frida-use/2026-10-01/report.md) documents
the 2026-10-01 experiments, including the check that all 31 imported files matched
the manifest.
