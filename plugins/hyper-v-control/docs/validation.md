# Validation

Validation date: October 1, 2026 (America/Chicago). Local Windows x64 build, .NET SDK 10.0.401, self-contained .NET runtime 10.0.12, official MCP C# SDK 2.2.0.

The designated live target was `Windows 11 AV Test`, a Generation 2 Windows 11 guest (build 22621), initially Off. A dedicated baseline checkpoint was created before mutations; existing checkpoint branches were retained. The user explicitly authorized temporarily changing Secure Boot and guest BCD for kernel debugging, with restoration afterward.

## Measured behavior

| Area | Evidence |
| --- | --- |
| Build | C# compilation and single-file publish succeeded with zero warnings/errors |
| MCP protocol | Official C# client initialized both the EXE and packaged PowerShell launcher over stdio and discovered 36 unique tools; invalid path/host access failures returned structured errors |
| Elevation | Same-user medium-token parent connected to an elevated worker; authenticated named-pipe bridge carried normal tool responses and PNG images |
| VM/checkpoints | Inventory/detail, checkpoint creation, start, graceful shutdown/restart, pause/resume and save/start exercised on the designated VM |
| Guest execution | Correct zero/nonzero exit codes, distinct stdout/stderr, UTF-8 content; native executable with empty argv and explicit argv/exit-code cases |
| Files | Host→guest→host transfer with spaces and non-ASCII names; returned bytes matched exactly; bounded base64 read exercised |
| Basic console | Native 1024×768 PNG delivered through MCP; mouse clicked the password field, saved credentials signed in, keyboard opened Notepad |
| Semantic UI | Official winapp 0.7.1 downloaded with release SHA-256 verification; window listing and JSON inspection returned Notepad's actual element tree |
| Enhanced console | Actual Enhanced connection established; background capture rejected as designed instead of returning host pixels |
| Modern debugging tools | Official Microsoft WinDbg package 1.2606.22001.0, KD/CDB file version 10.0.29617.1000; Microsoft signature validated |
| User-mode debugging | CDB launch, consecutive commands, registers, stack, modules, continue/break and detach; target PID remained alive after detach; final in-guest persistent scheduled worker accessed through PowerShell Direct |
| KDCOM | Off-state COM mapping followed by running-guest BCD configuration; real kernel connection, `vertarget`, registers/stack, detach, and successful guest command after detach |
| KDNET | Configuration with generated key, virtual-switch IPv4 and UDP port 50009; real kernel connection, commands, detach, and successful guest command after detach |
| Skill | Bundled skill creator validator passed |
| Packaging | Portable plugin and MCP manifests passed their declared official JSON schemas; launcher hash validation and PowerShell parsing passed |
| Release isolation | Rizin selects its runtime beyond 100 unrelated releases; tag/asset filtering rejects other plugin streams, drafts and prereleases. Hyper-V downloads an exact pinned asset, reuses matching installations and leaves Rizin/PATH intact; corrupt bytes are rejected |
| Reproducibility | Publishing from a separate checkout path produced the same pinned EXE SHA-256; CI checks the runtime pin after rebuilding with the fixed SDK/runtime |
| Language | Plugin metadata, skill, documentation, MCP descriptions and implementation messages are English |

Raw local results and screenshots are written under ignored `build/hyperv-control/validation`; guest credentials are never logged. Connection keys in diagnostic results are test data and are invalidated when the baseline is restored. These logs are not packaged into the plugin.

Cleanup completed: the VM is Off, Secure Boot was read back as enabled, both COM port paths are empty, all 27 original checkpoint IDs are preserved, and the original current-parent branch was restored. The dedicated validation checkpoint was removed. Guest tool installations and debugging BCD changes were rolled back with the checkpoint.

## Practical limits

- This host did not present an interactive UAC consent dialog during testing. The split-token elevation path ran successfully, including the owner-SID mismatch fix; UAC-on consent/cancellation and alternate-admin-account behavior still require separate interactive testing. The bridge intentionally supports the same Windows user only.
- Enhanced connection and rejection of obscured/background capture were measured. A successful foreground Enhanced screenshot, clipboard roundtrip and its full input matrix were not certified by this run. Basic screenshot/input and guest UI Automation provide independently tested channels.
- Live create/remove/import/export, large disk resizing, multi-host management, every guest OS version, and every debugger command/extension were not exhaustively exercised. The local host is the management boundary; unsupported guest services return errors.
- KD needs a real Windows console even when its streams are redirected. The final backend uses a private hidden console per session and Ctrl+Break scoped to it. The earlier `-wake`/remote-interrupt experiments are not the shipped backend.
- Symbol download and first attachment can outlast a single call. Pending commands remain in the same session for poll/break; a launched process or successful BCD update is not reported as a verified attachment.
- No GitHub Release is published by the local build. `scripts/install.ps1` requires the matching published asset; the prebuilt plugin ZIP contains the EXE and works without downloading it.

## Review regression checks

The three review findings are covered by `tests/HyperVControl.Tests/ReviewRegressionTests.cs`, included in the normal `unit` phase:

- Both host and guest UI quoting preserve ASCII apostrophes and U+2018/U+2019/U+201A/U+201B exactly. Embedded command text remains data under the real Windows PowerShell parser.
- The exact guest child-process wrapper runs 13,001- and 200,000-character scripts with multibyte Unicode content through stdin. The tests check execution at the script tail, separate UTF-8 stdout/stderr, nonzero exit codes, empty-input EOF, timeout termination and over-limit rejection. Only a fixed bootstrap remains on the command line.
- Guest debugger startup failures using either `success=false` or `ok=false` produce an MCP error while preserving the worker ID, task name and diagnostic payload. Contradictory flags cannot override an explicit failure; successful startup remains successful.

These checks ran locally under Windows PowerShell without connecting to or modifying a VM. The child-process timeout case required execution outside the restricted development sandbox so `taskkill` could terminate the test's own process tree.

## Reproduction

Build with `scripts/build-hyperv-control.ps1`; run the C# harness in `unit` and `smoke` modes. `launcher` exercises the packaged PowerShell entrypoint. Live phases are explicit: `prepare`, `guest`, `ui`, `debug`, `kernel-serial`, `kernel-net`, and `cleanup`. They require a dedicated Off test VM and authorization for the changes involved. `HYPERV_CONTROL_TEST_VM` selects it; `HYPERV_CONTROL_TEST_CREDENTIAL_FILE` can supply a per-VM credential JSON for preparation. Archive prior validation output before another prepare: the harness refuses to overwrite its recovery checkpoint record. Always complete cleanup after an interrupted live run.
