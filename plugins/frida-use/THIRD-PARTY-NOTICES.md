# Third-party notices

Original Frida Use code and guides are covered by [LICENSE](LICENSE).
The full local-project import is mapped in the repository's
[docs/archive/frida-use/import-manifest.json](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/blob/main/docs/archive/frida-use/import-manifest.json).
The plugin keeps both upstream license texts under `licenses/` because its icon
derives from frida-website and its maintained
[anti-debug guide](skills/frida-use/references/anti-debug-bypass.md) derives from
Anti-Debug-DB.

## Frida website documentation and artwork

This plugin's `assets/icon.svg` and the inherited Frida website documentation
snapshots originate from [frida/frida-website](https://github.com/frida/frida-website).
The snapshots are archived in the repository under
`docs/archive/frida-use/references/frida-docs/`, outside the installed plugin.
The original export did not record its upstream revision; its exact imported
bytes are recorded in the import manifest. These are not claimed as current
upstream documentation.

The upstream MIT notice is retained in [licenses/frida-website-MIT.txt](licenses/frida-website-MIT.txt),
retrieved from revision `b0d962b53c9043fe70c6a5e51c2cf28bd66f73ea` on 2026-10-01.
Copyright notices include Tom Preston-Werner, Ole André Vadla Ravnås, and Karl
Trygve Kalleberg. The Frida name and inherited logo identify the toolkit;
this independently maintained plugin does not imply upstream endorsement.

## Anti-Debug-DB

The inherited Anti-Debug-DB snapshots originate from
[CheckPointSW/Anti-Debug-DB](https://github.com/CheckPointSW/Anti-Debug-DB) and are
archived in the repository under `docs/archive/frida-use/references/anti-debug-db/`,
outside the installed plugin. The original export did not record its upstream
revision. The upstream MIT notice is retained in
[licenses/anti-debug-db-MIT.txt](licenses/anti-debug-db-MIT.txt), retrieved from
revision `289d3cd9b0c37450f90d14891ba1625f4b0d97ff` on 2026-10-01, including its
copyright notice for Hemang Kumar.

## Research sources

Microsoft documentation and independent Chinese/English articles are linked and
summarized in the maintained guides. Their text is not redistributed wholesale.
Frida binaries and third-party Python packages are installed separately and
retain their own licenses.
