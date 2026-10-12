# Frida CLI tools cheatsheet (Windows)

Reference for every `frida-*` command line tool from `frida-tools`. Assumes frida-tools is already installed and on PATH (install guide → project README). For **local Windows targets you need no `frida-server`** — local injection ships in the `frida` core wheel. Verify with `frida --version`.

All flags below are verbatim from the `argparse` definitions in `frida_tools\`. Tools fall into two groups: those that need a **device** (`-D/-U/-R/-H`) and those that also need a **target** (`-f/-n/-p`...). The two universal options (`-O`, `--version`) are on every tool.

---

## frida (REPL)

Interactive REPL injecting a JavaScript agent into a target. Live script loading, `eval`, CodeShare, CModule, magic commands.

| Flag | Meaning |
|---|---|
| `-l SCRIPT, --load SCRIPT` | Load JS/TS script. **Repeatable**; auto-reloads on file change. |
| `-P JSON, --parameters JSON` | Parameters as JSON, passed to `init(stage, parameters)` (Gadget shape). |
| `-C CMODULE, --cmodule CMODULE` | Load CModule (C source or prebuilt native blob); exposed as global `cm`. |
| `--toolchain {any,internal,external}` | Toolchain for compiling the CModule (default `any`). |
| `-c URI, --codeshare URI` | Load a script from codeshare.frida.re (prompts to trust first use). |
| `-e CODE, --eval CODE` | Evaluate CODE before the prompt. **Repeatable**. |
| `-q` | Quiet mode: no prompt/banner, **quit after running `-l` and `-e`**. |
| `-t SECONDS, --timeout SECONDS` | In quiet mode, seconds to wait before terminating (`inf` = forever). |
| `--pause` | Leave main thread paused after spawn (default is `resume`). |
| `-o LOGFILE, --output LOGFILE` | Tee REPL log output to a UTF-8 logfile. |
| `--eternalize` | Eternalize the script before exit (keeps running after Frida detaches). |
| `--exit-on-error` | Exit with code 1 after any uncaught exception in the script. |
| `--kill-on-exit` | Kill the spawned program when Frida exits (only with `-f`). |
| `--auto-perform` | Wrap entered code in `Java.perform` (Android/Java). |
| `--auto-reload` / `--no-auto-reload` | Enable/disable auto-reload of scripts & CModule (**on by default**). |

```powershell
# Attach by PID, auto-reload hooks.js on save
frida -p 1234 -l C:\scripts\hook.js

# Spawn SUSPENDED, install hooks before entry point, then %resume at the prompt
frida -f C:\Windows\System32\notepad.exe --pause -l C:\scripts\agent.js

# Inline eval against a running process, quiet one-shot
frida -n notepad.exe -e "console.log(Process.arch, Process.platform)" -q

# Spawn, V8 runtime, pass params, kill target on quit (PowerShell quoting)
frida -f C:\malware\sample.exe -l agent.js --runtime v8 -P '{"verbose":true}' --kill-on-exit
```

Spawn-vs-attach: `frida -f app.exe` spawns (auto-resumes unless `--pause`); bare `frida app.exe` or `frida 1234` attaches. History lives in `%LOCALAPPDATA%\frida\State\history`.

---

## frida-ps

List processes (or applications) on a device.

| Flag | Meaning |
|---|---|
| `-a, --applications` | List only applications. |
| `-i, --installed` | Include all installed applications (error if used without `-a`). |
| `-j, --json` | Output as JSON. |
| `-e, --exclude-icons` | Exclude icons in output. |

```powershell
frida-ps                       # local processes (proves native core loaded)
frida-ps | Select-String notepad
frida-ps -U -ai                # USB device: all installed apps
frida-ps -H 127.0.0.1:27042 -j # remote frida-server, JSON
```

---

## frida-trace

Dynamically trace function calls (native C, ObjC, Swift, Java, imports, instruction offsets), auto-generating editable JS handlers and a live Web UI. Runs until Ctrl-C.

**Most useful flags**

| Flag | Meaning |
|---|---|
| `-i FUNC, --include FUNC` | Include `[MODULE!]FUNCTION` (glob). |
| `-x FUNC, --exclude FUNC` | Exclude `[MODULE!]FUNCTION` (glob). |
| `-I MODULE, --include-module MODULE` | Include **all** functions in modules matching the glob (e.g. `*.dll`). |
| `-X MODULE, --exclude-module MODULE` | Exclude a whole module. |
| `-a MODULE!OFFSET, --add MODULE!OFFSET` | Trace an **unexported** function by hex entry-point offset. |
| `-T, --include-imports` | Include the program's imports. |
| `-t MODULE, --include-module-imports MODULE` | Include a specific module's imports. |
| `-m / -M OBJC_METHOD` | Include / exclude ObjC method (e.g. `-m "-[NSView drawRect:]"`). |
| `-j / -J JAVA_METHOD` | Include / exclude Java method (glob + `/flags`, e.g. `isu`). |
| `-y / -Y SWIFT_FUNC` | Include / exclude Swift function. |
| `-s DEBUG_SYMBOL, --include-debug-symbol` | Include by debug symbol. |
| `-d, --decorate` | Add module name to the generated `onEnter` log line. |
| `-q, --quiet` | Do not format output messages. |
| `-S PATH, --init-session PATH` | JS file to initialize the session (repeatable); populates shared `state`. |
| `-P JSON, --parameters JSON` | Parameters as JSON, exposed as global `parameters`. |
| `-o OUTPUT, --output OUTPUT` | Dump messages to file. |
| `--ui-host / --ui-port / --ui-allow-origin` | Web UI binding (default `localhost`). |

**`-i`/`-x` glob forms** (MODULE and FUNCTION are both globs):

| Form | Meaning |
|---|---|
| `MODULE!FUNCTION` | Function in that module, e.g. `"msvcrt.dll!*cpy*"`. |
| `FUNCTION` | That function in **all** modules, e.g. `"*free*"`. |
| `!FUNCTION` | Same as plain `FUNCTION`. |
| `MODULE!` | Whole module, e.g. `"gdi32.dll!"` (same as `-I "gdi32.dll"`). |

**Working set is procedural, not declarative** — each `-I/-X/-i/-x` mutates the current set **in command-line order**. `-i "str*" -i "mem*" -X "msvcrt.dll"` (exclude last) differs from putting `-X` first (where it removes from an empty set and has no effect).

```powershell
# Trace file I/O on a running process
frida-trace -p 1234 -i "CreateFileW" -i "ReadFile" -i "WriteFile"

# All mem* functions in msvcrt, decorated with module name, spawn notepad
frida-trace --decorate -i "msvcrt.dll!*mem*" notepad.exe

# Winsock send/recv, module-qualified
frida-trace -p 1234 -i "ws2_32.dll!send" -i "ws2_32.dll!recv"

# Include all *open* but drop msvcrt's variant (order matters)
frida-trace -p 1234 -i "*open*" -x "msvcrt.dll!*open*"

# Unexported function by offset
frida-trace -p 1234 -a "gdi32full.dll!0x3918DC"

# Read extra options from a file (one shot)
frida-trace -p 9753 --decorate -O additional-options.txt
```

**The `__handlers__` workflow.** For each matched function frida-trace writes an editable stub to `<cwd>\__handlers__\<module>\<function>.js`:

```js
defineHandler({
  onEnter(log, args, state) {
    log('CreateFileW()');
  },

  onLeave(log, retval, state) {
  }
});
```

Edit the stub to decode arguments — saving it hot-reloads into the running agent (a `frida.FileMonitor` watches each `__handlers__` subdir; ~50 ms debounce, no restart). A Windows `CreateFileW` handler:

```js
defineHandler({
  onEnter(log, args, state) {
    this.path = args[0].readUtf16String();   // LPCWSTR -> wide string
    log(`CreateFileW("${this.path}")`);
  },
  onLeave(log, retval, state) {
    log(`  -> ${retval}  GLE=${this.lastError}`);
  }
});
```

With `--decorate` the log line becomes `log('CreateFileW() [kernel32.dll]');`. Handlers can also be muted / given backtraces via the Web UI at `http://localhost:<ui_port>/`.

**`-P` quoting on Windows**: only double quotes work, so double inner quotes: `-P "{""displayPid"": true}"`. Java tracing usually wants `--runtime=v8`.

---

## frida-discover

Use Stalker to discover internal/hot functions being called in a process (per-thread call-count profiling). Press ENTER to stop and print results, grouped by module and sorted by call count. No tool-specific flags.

```powershell
frida-discover -p 1234
frida-discover -f C:\app\app.exe
```

---

## frida-kill

Kill a process on a device by name or PID. Needs a device, no target. A file path is rejected.

```powershell
frida-kill 1234
frida-kill -U Calculator
```

---

## frida-ls-devices

List all connected Frida devices (Id, Type, Name, OS) in a live table. Has **no** device-selection flags — only `-O` and `--version` apply.

```powershell
frida-ls-devices
```

---

## frida-itrace

Instruction-level tracer; follows a thread or address range via Stalker and writes a binary `.itrace` file (magic `ITRC`). If strategy/output are omitted it prompts interactively.

| Flag | Meaning |
|---|---|
| `-t THREAD_ID, --thread-id THREAD_ID` | Trace this thread id. |
| `-i THREAD_INDEX, --thread-index THREAD_INDEX` | Trace by thread index. |
| `-r RANGE, --range RANGE` | Trace a range, e.g. `0x1000..0x1008`, `kernel32.dll!CreateFileW`, `module!0x1234`, `recv..memcpy`. |
| `-o OUTPATH, --output OUTPATH` | Output file. |

Code-location forms: `0xADDR`, `module!0xOFF`, `module!export`, bare `symbol`.

```powershell
frida-itrace -p 1234 -i 0 -o C:\traces\main.itrace
frida-itrace -p 1234 -r "kernel32.dll!CreateFileW..kernel32.dll!ReadFile" -o C:\traces\io.itrace
```

---

## frida-join

Make a target process join a Frida Portal (control plane). Runs until Ctrl-C. Usage: `frida-join [options] target portal-location [portal-certificate] [portal-token]`.

| Flag | Meaning |
|---|---|
| `--portal-location LOCATION` | Join portal at LOCATION (required; positional `args[0]` overrides). |
| `--portal-certificate CERTIFICATE` | Speak TLS with portal, expecting CERTIFICATE. |
| `--portal-token TOKEN` | Authenticate with portal using TOKEN. |
| `--portal-acl-allow TAG` | Limit portal access to channels with TAG (repeatable). |

```powershell
frida-join -p 1234 127.0.0.1:27052
frida-join -U -n Calculator 10.0.0.5:27052 C:\certs\portal.crt mytoken
```

---

## frida-create

Scaffold a new Frida project — a TypeScript agent or a C CModule — with boilerplate.

| Flag | Meaning |
|---|---|
| `-t TEMPLATE, --template TEMPLATE` | `agent` or `cmodule` (**required**; unknown type errors out). |
| `-n PROJECT_NAME, --project-name PROJECT_NAME` | Project name (default = basename of cwd). |
| `-o OUTDIR, --output-directory OUTDIR` | Output directory (default `.`). |

Agent template emits `package.json` (with `frida-compile` build/watch scripts), `tsconfig.json`, `agent/index.ts`, `agent/logger.ts`, `.gitignore`. CModule emits `meson.build`, `<name>.c`, bundled `include/*` headers.

```powershell
frida-create -t agent -n my-hook -o C:\projects\my-hook
frida-create -t cmodule
```

---

## frida-rm / frida-pull / frida-push

Filesystem ops on a device (need a device, **not** a target). Run an `fs_agent.js` over a stream against the worker PID (frida-server / Gadget).

**frida-rm** — `frida-rm [options] FILE...`
- `-f, --force` — ignore nonexistent files
- `-r, --recursive` — remove directories recursively

**frida-pull** — `frida-pull [options] REMOTE... LOCAL` (multi-file: last arg is the dest dir; single arg pulls into cwd using the remote basename).

**frida-push** — `frida-push [options] LOCAL... REMOTE` (last arg is the remote target; a single arg errors).

```powershell
frida-rm   -U /data/local/tmp/foo.txt
frida-rm   -U -r -f /sdcard/dir
frida-pull -U /data/local/tmp/app.log C:\dump\app.log
frida-push -U C:\tools\frida-gadget.so /data/local/tmp/
```

---

## frida-apk

Make an Android APK debuggable (`android:debuggable=true`) and optionally inject a Frida gadget. Operates purely on local files (no device).

| Flag | Meaning |
|---|---|
| `-g GADGET, --gadget GADGET` | Inject the specified gadget library (arch auto-detected from its ELF header). |
| `-c GADGET_CONFIG, --gadget-config KEY=VALUE` | Set a gadget interaction config entry (repeatable; errors without `-g`). |
| `-o OUTPUT, --output OUTPUT` | Output path (default: input `.apk` → `.d.apk`). |

```powershell
frida-apk C:\apps\app.apk
frida-apk -g C:\gadget\arm64\libfridagadget.so -o C:\apps\app.patched.apk C:\apps\app.apk
frida-apk -g C:\gadget\arm64\libfridagadget.so -c on_load=resume C:\apps\app.apk
```

---

## frida-pm

Frida package manager — search/install npm-style Frida packages (bridges, etc.) into a project's `node_modules`. No device. Uses subcommands; top-level `--registry HOST`.

**`search`**: positional `query`; `--offset N`, `--limit N`, `--json`.
**`install`**: positional `specs` (e.g. `frida-objc-bridge@^8.0.6`); `--project-root DIR`, `-P/--save-prod` (default), `-D/--save-dev`, `--save-optional`, `--omit {dev,optional,peer}`, `--quiet`.

```powershell
frida-pm search trace
frida-pm install frida-il2cpp-bridge --project-root C:\projects\my-agent
frida-pm install frida-objc-bridge@^8.0.6 -D --json
```

Other scripts the package installs but not detailed here: `frida-ls` (list device files), `frida-strace` (syscall tracer), `frida-compile` (bundle a TS/JS agent).

---

## Common options (all tools)

Inherited from `ConsoleApplication`. The device group appears only when a tool needs a device; the target group only when it spawns/attaches.

### Universal

| Flag | Meaning |
|---|---|
| `-O FILE, --options-file FILE` | Read additional CLI options from a text file (nestable; recursive `-O` expansion; same file twice is an error). |
| `--version` | Print `frida.__version__` and exit. |

### Device selection

| Flag | Meaning |
|---|---|
| `-D ID, --device ID` | Connect to device with this ID. |
| `-U, --usb` | Connect to USB device. |
| `-R, --remote` | Connect to remote frida-server (default host). |
| `-H HOST, --host HOST` | Connect to remote frida-server on HOST (e.g. `-H 192.168.1.5:27042`). |
| `--certificate CERT` | Speak TLS with HOST, expecting CERT. |
| `--origin ORIGIN` | Set the `Origin` header for the remote connection. |
| `--token TOKEN` | Authenticate with HOST using TOKEN. |
| `--keepalive-interval N` | Keepalive seconds; 0 disables, -1 = auto (default). |
| `--device-option opt` | Override a backend-specific option, e.g. `control-endpoint=(string)...` (repeatable). |
| `--p2p` | Establish a peer-to-peer connection with the target. |
| `--stun-server ADDRESS` | STUN server for `--p2p`. |
| `--relay address,user,pass,turn-{udp,tcp,tls}` | Add a relay for `--p2p` (repeatable). |

Only one of `-D/-U/-R/-H` may be specified. Env fallbacks: `FRIDA_DEVICE`, `FRIDA_HOST`, `FRIDA_CERTIFICATE`, `FRIDA_ORIGIN`, `FRIDA_TOKEN`, `FRIDA_DEVICE_OPTIONS`. For local Windows analysis you typically use **none** of these — the default device is `local`.

### Target selection

| Flag | Meaning |
|---|---|
| `-f TARGET, --file TARGET` | **Spawn** FILE (held suspended; auto-resumes unless `--pause`). |
| `-F, --attach-frontmost` | Attach to the frontmost application (mobile). |
| `-n NAME, --attach-name NAME` | Attach by process name (e.g. `-n notepad.exe`). |
| `-N IDENTIFIER, --attach-identifier IDENTIFIER` | Attach by bundle/app identifier (mobile). |
| `-p PID, --attach-pid PID` | Attach by PID. |
| `-W PATTERN, --await PATTERN` | Gated spawn: await a spawn matching PATTERN (regex; enables spawn gating). |
| `--stdio {inherit,pipe}` | Stdio behavior when spawning (default `inherit`). |
| `--aux option` | Aux spawn option, e.g. `--aux uid=(int)42` (types: string,bool,int; repeatable). |
| `--realm {native,emulated}` | Realm to attach in (default `native`). |
| `--exceptor {full,handler-only,off}` | Exception-handling mode. |
| `--disable-unwind-broker` / `--disable-exit-monitor` / `--disable-thread-suspend-monitor` | Disable the named subsystem. |
| `--linker-notifier-offset OFFSET` | Add a linker notifier offset (repeatable). |
| `--runtime {qjs,v8}` | JS runtime: QuickJS (default, small/fast) or V8 (full ES2015+, better stacks). |
| `--debug` | Enable the Node.js-compatible script debugger (prints "Chrome Inspector server listening on port 9229"). |
| `--squelch-crash` | Do not dump a crash report to console. |

**Target inference** (bare positional `args`): a value starting with `.`, the path separator, or a `X:\` drive pattern → **spawn** (`file`); an all-digit value → **PID**; otherwise a process **name**. So `frida -f app.exe` spawns, `frida 1234` attaches by PID, `frida notepad.exe` attaches by name.

---

## Frida REPL magic commands

The **only** built-in magic commands (from `_repl_magic.py`). Bare `help` is aliased to `%help`; `exit`/`quit`/`q` leave the REPL; a trailing `?` on an expression (`Process?`) prints type/signature help.

| Command | Args | What it does |
|---|---|---|
| `%resume` | 0 | Resume execution of the spawned (paused) process. |
| `%load <path>` | 1 | Load an additional script and reload REPL state (prompts to discard current state; appends to the user scripts). |
| `%reload` | 0 | Reload (rerun) the script given to the REPL with `-l`. |
| `%unload` | 0 | Unload the currently loaded script. |
| `%autoperform <on\|off>` | 1 | When on, wrap any entered REPL code in `Java.performNow()` (Android/Java). |
| `%autoreload <on\|off>` | 1 | Enable/disable auto-reloading of script files. |
| `%exec <path>` | 1 | Execute the given file in the context of the currently loaded scripts. |
| `%time <expr>` | 1+ | Measure and print the execution time of the expression (ms). |
| `%help` | 0 | Print the list of available REPL commands. |

> **No `%hexdump`, `%dump`, `%dis`, `%sym`, `%backtrace`, or `%dis`.** These are **not** built-in magic commands in current Frida. What actually exists:
> - **Hexdump is automatic**: returning an `ArrayBuffer`/binary value at the prompt auto-renders as a hexdump. The JS global `hexdump(ptr, { length, header, ansi })` is also always available.
> - **Quick commands use a `.` prefix** (not `%`) and ship with **zero** registered by default. Register your own from an agent:
>   ```js
>   REPL.registerQuickCommand('dis', {
>     minArity: 1,
>     onInvoke: (addr) => Instruction.parse(ptr(addr)).toString()
>   });
>   // then at the prompt:  .dis 0x401000
>   ```
> - Symbolication / backtraces are done inline in JS: `DebugSymbol.fromAddress(ptr('0x...'))`, `DebugSymbol.getFunctionByName(...)`, `Thread.backtrace(this.context, Backtracer.ACCURATE).map(DebugSymbol.fromAddress)`.
> - `:dtf`-style format tokens belong to **r2frida** (a separate radare2 plugin), not the stock REPL.

### REPL launch flags (recap)

```powershell
frida -f app.exe -l hooks.js --pause       # spawn-and-hold; %resume to release
frida -n target.exe -l hooks.js            # attach + auto-reload hooks.js on save
frida -p 4242 -l dump.js -q -o run.log     # one-shot: run script vs PID, log, exit
frida -n notepad.exe -e "console.log(Process.arch)"   # inline eval (repeatable -e)
frida -f app.exe -l agent.js -P "{""verbose"":true}"  # parameters JSON (Windows quoting)
frida -p 1234 -l hooks.js --debug          # open the Node-inspector debugger (port 9229)
```

- `-l` is repeatable and auto-reloads by default (toggle with `--no-auto-reload` / `%autoreload off`). TypeScript `.ts` scripts are auto-compiled.
- `-q` suppresses the banner/prompt and exits after `-l`/`-e`; pair with `-t SECONDS` (or `inf`) to bound the run.
- `-o LOGFILE` tees all `console.log`/error output to a UTF-8 file.

---

### Windows quick reference

- **No frida-server needed** for local `.exe` analysis; it is only for driving this box from another machine (run `frida-server.exe`, connect with `-H`).
- **Install into 64-bit Python** on x64 Windows — it injects into both 32- and 64-bit targets; a 32-bit Python can only touch WOW64 targets.
- **Run the terminal as Administrator** to attach to elevated/SYSTEM processes (`Failed to attach: access denied` otherwise). PPL processes (e.g. `lsass` with RunAsPPL) cannot be injected even as admin.
- Spawn suspended (`-f ... --pause`) to land hooks before the entry point — essential for anti-debug/unpacking work; release with `%resume`.