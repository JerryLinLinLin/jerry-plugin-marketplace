# Debugging

Keep target scope explicit. `debug_open` PIDs/paths belong to the HOST; `guest_debug_open` PIDs/paths belong to the VM. Kernel tools attach to a remote VM only, never the host kernel.

## User mode inside a VM

Use `guest_debug_setup` to copy the published EXE and selected current WinDbg engine directory into the guest. This needs a guest administrator credential. Then `guest_debug_open` with mode `launch`, `attach`, or `dump`, and the returned guest debugger directory. It starts a bounded scheduled worker that survives the PowerShell Direct call. Save workerId and taskName. Poll its status before commands.

Use `guest_debug` action `command` for `bp/bu/bl/bc`, `g/t/p`, `k`, `r`, `db/dq`, `lm`, `.sympath/.reload`, `!analyze` and ordinary CDB commands. A launch under the worker uses the selected account's elevated task token: do not label it an unprivileged reproduction. For a standard-user trigger, use that account's credential profile with `guest_process`; attach to its PID from the debugging account. Record the observed token when privilege level matters.

## Kernel mode

Record state/checkpoints first. Prefer KDNET when the guest can reach the host on a vSwitch. Pick a unique UDP port, then `kernel_configure` action `kdnet` with the reachable host IPv4 address. It verifies bcdedit exit codes, creates a full-strength key if omitted, and returns `kernelAttachString`. Preserve this key as sensitive connection material. Host firewall, guest Secure Boot/BitLocker and adapter support are separate prerequisites: inspect and make only changes authorized by the task. BCD success alone is not a debugger connection.

For KDCOM, use two phases: while Off call `serial_host` with a simple pipeName and COM port; start the VM and wait for PowerShell Direct; call `serial_guest` with the same values. Never attempt PowerShell Direct while Off/Saved. Guest BCD changes require a restart. Use `debug_open` mode `kernel` and the returned attach string, and verify KD's actual connection/initial break output. Enable restart only when within the requested experiment; configuration tools do not reboot by default.

Kernel break suspends the guest OS; PowerShell Direct and UI will stop responding. While broken, use KD tools, then `g` before trying guest commands again. A pending `g` is normal: use `debug_poll` or `debug_break`, not another queued debugger command. For early boot or repeated reboot debugging, named-pipe reconnect and KDNET remain persistent sessions.

## Session handling

Host `debug_command` and guest `guest_debug` return bounded output with pending state and cursor. Poll after the returned cursor to avoid repeated output. Timeout does not kill the debugger. A command still pending blocks another command; break and poll to complete it. Break signals only the session's private hidden Windows console; `-wake` is not a general target interrupt. `.shell` is disabled. Local symbol cache defaults to `%LOCALAPPDATA%\HyperVControl\symbols` with Microsoft's symbol server; `_NT_SYMBOL_PATH` or `debug_open(symbolPath=...)` can override it. First kernel symbol loading can take longer than a tool call: retain the same session and poll. An existing local-only path is useful for checking connectivity without waiting on symbol downloads.

Close by detaching using `debug_close` or guest `close`. Do not send bare `q` to a user-mode debugger: it may terminate the target. If detach times out, the session remains inspectable; resolve it before rollback. Export needed dumps/logs, close workers/consoles, and restore the intended VM state. Guest workers have a one-hour lifetime and unregister their task on normal exit; after an interrupted run inspect the returned taskName before removing it.

Microsoft references: [current WinDbg](https://learn.microsoft.com/windows-hardware/drivers/debugger/), [KD command line](https://learn.microsoft.com/windows-hardware/drivers/debugger/kd-command-line-options), [VM KDNET setup](https://learn.microsoft.com/windows-hardware/drivers/debugger/setting-up-network-debugging-of-a-virtual-machine-host), [PowerShell Direct](https://learn.microsoft.com/windows-server/virtualization/hyper-v/powershell-direct).
