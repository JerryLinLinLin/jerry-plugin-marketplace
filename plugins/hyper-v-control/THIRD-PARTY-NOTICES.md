# Source and runtime notices

Hyper-V Control's new integration code is covered by the repository MIT license.

The five files under `src/HyperVControl/Extracted` were extracted and adapted at the owner's request from the supplied RaivenX checkout, revision `b5971dda1fa2050dde36b8b22fda9726df92cb48`: `HyperVService.cs`, `HyperVModels.cs`, `HyperVConsoleManager.cs`, `HyperVGuestUiService.cs`, and `Rgb565Pixels.cs`. Their source comments identify that provenance. The extraction excludes RaivenX chat, WPF app shell, provider integrations, SSH and WSL features. No claim is made that this project's MIT notice replaces separately applicable upstream terms.

The supplied [originsec/hyperv-mcp](https://github.com/originsec/hyperv-mcp) checkout, revision `89d90ee529f150236249a0a7a9b72f14ebed6426`, was reviewed as a behavioral reference for its 19 tools and KDNET/KDCOM workflows. Its Python implementation is not bundled or executed. Upstream is Apache-2.0.

The self-contained EXE includes Microsoft .NET runtime/libraries (MIT and component notices) and the [official Model Context Protocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) (Apache-2.0). Exact NuGet packages and integrity hashes are recorded in `src/HyperVControl/packages.lock.json`. Upstream notices: [.NET runtime](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT), [Windows Desktop](https://github.com/dotnet/winforms/blob/main/LICENSE.TXT), [MCP SDK license](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/LICENSE).

Microsoft WinDbg/KD/CDB and winapp are separate Microsoft products, not included in the Hyper-V Control release. Debugger discovery uses the user's installation or explicitly supplied engine directory. The optional guest-setup operations copy locally available tools into a user-selected VM or download official winapp release assets, preserving the full tool directory and its notices. Microsoft software remains subject to its own license; this plugin does not relicense it.
