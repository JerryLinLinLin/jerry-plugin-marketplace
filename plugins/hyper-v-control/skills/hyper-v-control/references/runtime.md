# Runtime and permissions

The prebuilt plugin ZIP contains `bin/HyperVControl.exe`. A source-only marketplace install uses `scripts/install.ps1` to download the version/checksum pinned in `runtime.json`; then reconnect the MCP. The release must exist before this download path works. Development builds use the repository build script, never a Python MCP server.

The launcher verifies the EXE hash and starts `--elevate`. The parent keeps MCP stdin/stdout; a same-user worker receives them over a private named pipe. Only the current Windows user's SID has access, and both ends verify process IDs. UAC consent can appear once when a split-token administrator starts it. Cancellation is an error with no VM mutation. Do not disable UAC or grant permanent group membership to make a call work.

An ordinary account may run the EXE directly when granted the required Hyper-V rights. PowerShell Direct and some host operations require administrator rights beyond Hyper-V inventory access. The UAC bridge supports the same account's elevated token. Entering another administrator's credentials does not satisfy that identity boundary: run the MCP client/server under the intended administrator account instead. A locked or noninteractive host cannot display a console/UAC UI.

Guest credentials are separate from host elevation. `hyperv_credential` verifies them against the running VM, then stores them in Windows Credential Manager. Separate profiles can hold administrator and standard guest accounts. Environment fallback uses `HYPERV_CONTROL_GUEST_USERNAME/PASSWORD`, or `HYPERV_CONTROL_<PROFILE>_USERNAME/PASSWORD`. Never echo passwords. No credentials are bundled with the plugin.

Prefer current Microsoft WinDbg. `hyperv_debugger_discover` reports paths and file versions. `HYPERV_CONTROL_DEBUGGER_DIR` explicitly selects a full engine directory containing `kd.exe`, `cdb.exe`, dependent DLLs and extensions; do not copy just KD. Installed Microsoft.WinDbg MSIX is preferred over legacy Windows Kits. Install/update through Microsoft's documented WinDbg installer or `winget install Microsoft.WinDbg` when needed for the authorized task.
