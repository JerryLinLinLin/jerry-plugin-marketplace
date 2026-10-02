# frida-windows-skills

An AI Agent **skill** — [`frida-windows-re`](skills/frida-windows-re/SKILL.md) — for
dynamic instrumentation and security analysis of Windows binaries with
[Frida](https://frida.re) and the `frida-tools` CLI. It's a cheatsheet + bundled
reference docs for runtime/dynamic analysis on a Windows process, DLL, or `.exe`:
API hooking and tracing, dumping decrypted buffers / crypto keys, monitoring
file/registry/network activity, anti-debug / anti-Frida bypass, and
product-security / app-pentest work (TLS interception, cert-pinning bypass,
secret/key auditing, license/auth checks, fuzzing).

```
frida-windows-skills/
├── README.md                     ← you are here (setup / install)
└── skills/frida-windows-re/
    ├── SKILL.md                  ← the cheatsheet entry point
    └── references/               ← bundled reference docs (CLI, JS API, recipes, anti-debug, product-sec)
```

The skill itself **assumes `frida-tools` is already installed and on `PATH`**
(`frida --version` works). Install it once with the guide below, then use the skill.

---

## Install frida-tools (uv)

frida-tools ships **prebuilt wheels** for Windows — no compiler needed. The cleanest
install is as an isolated [`uv`](https://docs.astral.sh/uv/) tool.

Two PyPI packages, two roles: `frida-tools` is the CLI suite (`frida`, `frida-ps`,
`frida-trace`, …); `frida` is the native core + Python binding you `import frida`.
Installing `frida-tools` pulls `frida` automatically.

| Package | Provides | Pulled by |
|---|---|---|
| `frida-tools` | CLI commands | you install it |
| `frida` | native core wheel + `import frida` | dependency of `frida-tools` (`frida >= 17.10.0, < 18.0.0`) |

### Quick install (recommended)

```powershell
uv tool install frida-tools
uv tool update-shell      # add the tool bin dir to PATH (persistent) — then OPEN A NEW TERMINAL
frida --version           # verify (prints the frida CORE version, e.g. 17.15.3)
frida-ps                  # lists local processes -> proves the native core loaded
```

`uv tool install` creates a dedicated isolated venv, installs `frida-tools` + deps
(`frida`, `colorama`, `prompt-toolkit`, `pygments`, `websockets`), and drops `.exe`
shims for every `frida-*` command into uv's tool bin dir (`%USERPROFILE%\.local\bin`).

One-shot PATH fix for the **current** session (no new terminal):

```powershell
$env:PATH = "$env:USERPROFILE\.local\bin;$env:PATH"
uv tool dir --bin                 # prints the exact bin dir uv uses
```

Pick the interpreter for the tool's venv (uv can supply it — no system Python needed):

```powershell
uv tool install --python 3.12 frida-tools
```

## Verify the install

```powershell
frida --version            # frida core version, e.g. 17.15.3
uv tool list               # confirms frida-tools + its 17 exposed commands
Start-Process notepad.exe
frida-ps | Select-String notepad
frida-trace -n notepad.exe -i "CreateFileW"     # smoke test
```

If `frida --version` errors with a DLL-load failure, that's the VC++ runtime /
wrong-arch-wheel symptom — install the latest VC++ Redistributable (x64) and confirm a
supported interpreter/arch.

---

## Install this skill into your AI agent (global / user space)

Claude Code, Codex CLI, and GitHub Copilot all read the **same `SKILL.md` Agent Skill
format** from a personal skills dir — installing is just dropping the
`skills/frida-windows-re/` folder into each:

| Agent | Global skills dir (Windows) |
|---|---|
| Claude Code | `%USERPROFILE%\.claude\skills\` |
| OpenAI Codex CLI | `%USERPROFILE%\.codex\skills\` |
| GitHub Copilot | `%USERPROFILE%\.copilot\skills\` |

```powershell
$src = "C:\projects\frida-windows-skills\skills\frida-windows-re" # adjust to your clone
foreach ($a in '.claude', '.codex', '.copilot') {             # keep only the agents you use
  $dst = "$env:USERPROFILE\$a\skills"
  New-Item -ItemType Directory -Force $dst | Out-Null
  Copy-Item -Recurse -Force $src $dst
}
```

Each agent auto-discovers the skill by its `name`/`description` and loads it when a
task matches (in Claude Code, run `/skills` to confirm).
