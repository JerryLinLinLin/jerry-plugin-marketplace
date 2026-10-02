# CLI: targeted Windows work

Resolve the intended `frida` executable, then inspect its `--help`. Options are
owned by `frida-tools` and can differ between tools. These examples follow the
14.10.4 interface; do not infer that `frida-trace` accepts every REPL flag.

```powershell
frida --version
frida-ps
frida-ls-devices
frida -p 1234 -l ./hooks.js -o ./session.log
frida -f 'C:/lab/demo.exe' -l ./hooks.js -- --input 'C:/lab/test data.bin'
frida -f 'C:/lab/demo.exe' -l ./hooks.js --pause
```

The REPL normally resumes a spawned target after loading the script. `--pause`
holds it until `%resume`. `--no-pause` belongs to older tutorials. Python's
`device.spawn()` does not implement the REPL's auto-resume behavior.
Quiet `-q` exits after the requested work; use `-t 10` for a bounded observation
window or `-t inf` only for an intentionally persistent session. `--kill-on-exit`
on the REPL applies to its spawned process. An attach/detach is not a kill.

## Tracing

Run in a case directory so generated `__handlers__` files belong to that case.

```powershell
frida-trace -p 1234 -i 'KERNELBASE.dll!CreateFileW' -i 'KERNELBASE.dll!ReadFile'
frida-trace -p 1234 -i 'ws2_32.dll!recv' -i 'ws2_32.dll!send' -o ./network.log
frida-trace -p 1234 -a 'demo.exe!0x1234'
```

`-a` uses a module-relative memory offset (RVA), not a raw PE file offset. Replace
the example RVA with one verified for the exact build. Export forwards may make
different module names resolve to the same address; check the generated handlers.

Include/exclude options are applied in order to build a working set. Start with a
few functions instead of `-I ntdll.dll`. Use `-S shared.js` for common handler
helpers and `-P` for parameters only after checking quoting in the actual shell.

Generated handlers have a different signature from raw `Interceptor` callbacks:

```javascript
defineHandler({
  onEnter(log, args, state) {
    this.requested = args[2].toUInt32();
    this.count = args[3];
    this.overlapped = args[4];
  },
  onLeave(log, retval, state) {
    if (retval.toInt32() !== 0 && this.overlapped.isNull() && !this.count.isNull()) {
      try { log('ReadFile completed ' + this.count.readU32() + '/' + this.requested); }
      catch (e) { log('unreadable result: ' + e.message); }
    }
  }
});
```

`this` holds one invocation's state. `state` is shared state. A generated stub
does not know enough to validate your Windows prototype or asynchronous semantics.
Keep `__handlers__` under case version control when reproducibility matters.

Do not equate the CLI's exit code with the target's exit code. In the 14.10.4
tools, session detachment can print `Process terminated` and return 1 even after
useful tracing. Inspect the transcript and use an independent process exit-status
source when automation needs to distinguish normal termination from a crash.

## REPL and adjacent tools

Use `%help` for the installed magic commands. `%load`, `%reload`, `%unload`, and
`%resume` manage the session; evaluate `hexdump()`, `Instruction.parse()` and
`Thread.backtrace()` as JavaScript. Do not invent `%dump` or `%backtrace` commands.
`--debug --runtime v8` opens the JS Inspector; see the debugging guide.

`frida-discover` can help select functions for a narrower trace. `frida-compile`
builds TypeScript/npm agents for a Python `create_script` workflow. `frida-kill`
terminates a target; it is not a cleanup synonym for detach. Features of newer
tools such as syscall tracing or Barebone must be checked per backend before
promising Windows kernel coverage.

Sources: [CLI](https://frida.re/docs/frida-cli/),
[frida-trace](https://frida.re/docs/frida-trace/),
[trace session helpers](https://frida.re/docs/frida-trace/session-initialization-primer/),
[frida-tools source](https://github.com/frida/frida-tools).
