# Hyper-V Control extraction inventory

Sources inspected: local `C:\projects\raivenx` at `b5971dda1fa2050dde36b8b22fda9726df92cb48`, and `C:\projects\hyperv-mcp` at `89d90ee529f150236249a0a7a9b72f14ebed6426`. The scope is the entire Hyper-V control surface, not RaivenX's unrelated chat, browser, WSL or SSH products.

| RaivenX command / workflow | Hyper-V Control implementation |
| --- | --- |
| Capability probe, local inventory, VM IDs/state | `hyperv_capabilities`, `hyperv_list`, `hyperv_vm_info`; extracted HyperVService |
| Start, shutdown, turn off, graceful restart, pause/resume/save | `hyperv_power`; preserves bounded graceful restart and no hard-power fallback |
| Firmware Secure Boot | `hyperv_firmware`; Off/Generation 2 checks and read-back |
| Checkpoint tree/current branch | `hyperv_checkpoints`; current parent retained independently of timestamp order |
| Checkpoint create/restore/delete/name/ID/current | `hyperv_checkpoint`; rename added |
| Multiple Basic/Enhanced consoles, open/close/list | `hyperv_console`; standalone owned WinForms windows replace the WPF chat panels |
| Basic ActiveX configuration | Extracted `HyperVConsoleManager`; port 2179, VM GUID PCB, no invented EnhancedMode=0 |
| Enhanced session, fallback, scaling, dynamic resolution | Same manager; actual mode and fallback reason returned; no RaivenX preference database dependency |
| Basic native framebuffer + RGB565 conversion | `hyperv_screenshot`; inline MCP image and native pixel dimensions |
| Enhanced capture | Guarded foreground/window-occlusion checks before copying the RDP control pixels |
| Click/type/key/scroll, Ctrl+Alt+Delete | `hyperv_input`; synthetic WMI input in Basic, RDP input sink in Enhanced; move/down/up added for drag |
| Saved credential, verify, sign in | `hyperv_credential`, `hyperv_input(login)`; Windows Credential Manager replaces RaivenX's plaintext file |
| Enhanced host↔guest clipboard | `hyperv_clipboard`; explicit shared host clipboard semantics |
| Guest PowerShell Direct | `hyperv_guest_run`, `hyperv_guest_process`; child timeout, exit code, independent stdout/stderr |
| Copy file/folder in both directions | `hyperv_copy`; extracted literal/base64-safe copy scripts, parent directory creation and folder recursion |
| winapp guest download/install/checksum | `hyperv_guest_setup`; extracted release digest verification |
| winapp status/inspect/search/list-windows/invoke/click/focus/get-value/set-value/get-property/wait-for/scroll | `hyperv_ui`; extracted interactive task bridge, lock/session checks, optional elevation and per-VM serialization |
| UAC broker and chat console ownership | Same-user stdio elevation bridge with PID/SID authentication; standalone MCP ownership replaces chat handoff UI |

RaivenX-specific WPF tabs, tool permission cards, settings persistence and chat handoff confirmations are app-shell behavior, not bundled dependencies. A power transition can disconnect an ActiveX session; reopen the owned console after confirming VM state. The MCP reports this condition rather than relying on RaivenX's panel reconnection event handlers.

| Reference Python MCP tools | Coverage and changes |
| --- | --- |
| list_vms / get_vm_info / start_vm / stop_vm / reset_vm | Inventory/detail/power tools, with additional create/configure/import/export/remove |
| checkpoint_create/list/restore/remove | Checkpoint tools, including current branch and ambiguous-name rejection |
| configure_kdnet | Checked guest BCD exit codes, 256-bit generated key material, explicit reboot and host IP/port |
| configure_kdcom | Split `serial_host` (Off) and `serial_guest` (Running); fixes impossible PowerShell Direct invocation while Off/Saved |
| guest_run / guest_run_ps | `guest_process` / `guest_run`; Unicode argv handling and separate output streams |
| guest_put/get/read_file/list_dir | `copy` / `guest_files`; bounded reads and byte counts |
| victim_run / victim_run_ps | Separate standard-account credential profiles; token is determined by the actual guest account, not an unverified "victim" label |
| External kd-mcp dependency | Replaced with C# persistent KD/CDB session management and in-guest user-debug workers |

Additional gaps addressed: explicit debugger version/source discovery, current WinDbg preference, dump sessions, command continuation/polling, debugger interrupt and detach, guest-session isolation, single-file EXE packaging and plugin routing skill.

Authoritative references used: [Hyper-V PowerShell Direct](https://learn.microsoft.com/windows-server/virtualization/hyper-v/powershell-direct), [current WinDbg distribution](https://learn.microsoft.com/windows-hardware/drivers/debugger/), [DbgEng SetInterrupt](https://learn.microsoft.com/windows-hardware/drivers/ddi/dbgeng/nf-dbgeng-idebugcontrol-setinterrupt), [DebugConnectWide](https://learn.microsoft.com/windows-hardware/drivers/ddi/dbgeng/nf-dbgeng-debugconnectwide), [VM KDNET](https://learn.microsoft.com/windows-hardware/drivers/debugger/setting-up-network-debugging-of-a-virtual-machine-host), [winapp](https://github.com/microsoft/winappCli), [official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk), [OpenAI plugin packaging](https://developers.openai.com/plugins/build/plugins).

Each KD/CDB session runs through a C# helper owning a private hidden Windows console while command/output streams remain redirected. This supports KD's console requirements and limits Ctrl+Break to that session. The MCP process and other debugger sessions do not share the console. `kd/cdb -wake` only exits the special user-debugger sleep mode and is not a general target-break mechanism. No debugger remoting server or firewall-sharing ports are enabled. See [AllocConsole](https://learn.microsoft.com/windows/console/allocconsole) and [GenerateConsoleCtrlEvent](https://learn.microsoft.com/windows/console/generateconsolectrlevent).
