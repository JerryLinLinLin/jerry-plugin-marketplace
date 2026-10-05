# Frida Use

A plugin for dynamic instrumentation, debugging, and reverse engineering with Frida, focused on Windows. Both the plugin and skill display name are **Frida Use**, and both IDs are `frida-use`. Invoke the skill as `$frida-use` in Codex or `/frida-use:frida-use` in Claude Code, or let automatic skill selection match a relevant task.

The plugin includes all documentation, artwork, and licenses from the original project, together with maintained guides and reusable scripts. Installation and use are independent of the original project directory and Git repository.

## Contents

| Task | Reference |
| --- | --- |
| Skill workflow and reference routing | [SKILL.md](skills/frida-use/SKILL.md) |
| Installation, versions, and Python environments | [setup.md](skills/frida-use/references/setup.md) |
| Debugging Python controllers, injected JavaScript, and native processes | [windows-debugging.md](skills/frida-use/references/windows-debugging.md) |
| Python sessions, RPC, messages, binary data, and cleanup | [python-controller.md](skills/frida-use/references/python-controller.md) |
| API hooks, RVAs, ABIs, delayed DLL loading, and asynchronous I/O | [windows-hooking-recipes.md](skills/frida-use/references/windows-hooking-recipes.md) |
| Malware analysis, child processes, unpacking, and evidence capture | [malware-analysis.md](skills/frida-use/references/malware-analysis.md) |
| Reversible argument, return-value, function, and machine-code changes | [patching.md](skills/frida-use/references/patching.md) |
| Research findings, sources, and outdated practices | [research-sources.md](skills/frida-use/references/research-sources.md) |
| Imported material and provenance | [migration.md](docs/migration.md) |

## Install and use

Install **Frida Use** from Jerry's Plugin Marketplace in Codex or Claude Code; see the [marketplace setup](https://github.com/JerryLinLinLin/jerry-plugin-marketplace/blob/main/README.md#install). Refresh an existing marketplace with the command for your client:

```powershell
# Codex
codex plugin marketplace upgrade my-plugin-marketplace
# Claude Code
claude plugin marketplace update my-plugin-marketplace
```

The plugin provides a skill, Python helpers, and JavaScript examples. Install the Frida runtime in the analysis environment. No MCP server or account connection is required. An existing `frida-windows-re` installation is not renamed automatically; remove it after confirming that the new plugin works.

Create an isolated environment for Python scripts, and use the same interpreter for installation and execution:

```powershell
python -m venv .venv
$fridaPython = (Resolve-Path -LiteralPath './.venv/Scripts/python.exe').Path
& $fridaPython -m pip install frida-tools
& $fridaPython -c 'import frida; print(frida.__version__)'
```

The absolute path in `$fridaPython` remains valid after changing directories.
In the same PowerShell session, change to the plugin's `skills/frida-use` directory and run:

```powershell
& $fridaPython scripts/doctor.py
& $fridaPython scripts/frida_session.py --pid 1234 --out ./capture --duration 30
& $fridaPython scripts/frida_session.py --spawn 'C:/lab/demo.exe' --out ./capture-startup --kill-on-exit
```

The default agent observes file opens and records at most 200 events. Select a custom agent with `--script`; it must send `send({type: 'ready'})` after installing its initial hooks.
The runner writes UTF-8 `events.jsonl` and binary attachments with SHA-256 hashes. Capture duration defaults to 30 seconds. It records dropped events, truncation, JavaScript errors, available Windows exit codes, and Frida crash information.
`--kill-on-exit` applies only to a process spawned by the runner. Attach mode detaches on exit.

## Scope

Frida and frida-tools have independent release cycles. Use the [setup guide](skills/frida-use/references/setup.md) to check your installed environment and the upstream release notes for compatibility changes.
Windows user-mode x86/x64 is the main focus. Check ARM64, remote server/Gadget, Barebone, managed runtimes, and third-party GUI capabilities separately.

## License

Original content: [MIT](LICENSE). Imported Frida website documentation, artwork, and Check Point Anti-Debug-DB material retain their upstream terms; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Research articles are linked and summarized rather than reproduced in full.
