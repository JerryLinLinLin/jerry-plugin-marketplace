using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HyperVControl.Services;

namespace HyperVControl.Tests;

/// <summary>
/// Live validation of golden image builds through the published EXE, without downloading Windows media or creating VMs.
/// A synthetic WIM (placeholder hives, the host's boot files, BCD template and bootstr.dll, and a marker) wrapped in an
/// IMAPI2 ISO drives the full Windows pipeline into a small VHDX under D:\HyperVControl-validation\imagebuild; the result
/// is mounted read-only and its partitions, files, unattend, tools and offline BCD store (entries and system-store flags)
/// are checked. A second WIM with no boot files must fail at bcdboot with its reason and leave nothing behind. For Linux,
/// the qemu-img precondition is checked without any download, and a build from a small .vhdx exercises copy, Resize-VHD
/// and a real CIDATA seed ISO.
/// Preparation, verification and cleanup that need administrator rights run in elevated helper scripts; everything the
/// test creates lives under one run directory that is detached and deleted in every outcome.
/// </summary>
internal static class ImageBuildLiveTests
{
    private const string ValidationRoot = @"D:\HyperVControl-validation\imagebuild";

    internal static async Task RunAsync(ToolCall call, Action<bool, string> check, string results)
    {
        var run = Path.Combine(ValidationRoot, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        if (Directory.Exists(run)) throw new InvalidOperationException($"{run} already exists.");
        Directory.CreateDirectory(run);
        var evidence = new Dictionary<string, object?> { ["runDirectory"] = run, ["startedAt"] = DateTimeOffset.UtcNow };
        var output = Path.Combine(run, "out");
        var marker = Guid.NewGuid().ToString("D");
        var password = "Hvc!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        var kdKey = string.Join('.', Enumerable.Range(0, 4).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()));
        const string sshKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHyperVControlValidationKeyNotARealKey000000 validation@hyperv-control";
        Exception? failure = null;
        try
        {
            var tools = Path.Combine(run, "tools");
            Directory.CreateDirectory(Path.Combine(tools, "bin"));
            File.WriteAllText(Path.Combine(tools, "readme.txt"), "tools " + marker);
            File.WriteAllBytes(Path.Combine(tools, "bin", "\u7a7a \u95f4.dat"), RandomNumberGenerator.GetBytes(4096));
            File.WriteAllText(Path.Combine(tools, "hidden.cfg"), "hidden");
            File.SetAttributes(Path.Combine(tools, "hidden.cfg"), FileAttributes.Hidden);

            var prep = await ElevatedAsync(run, "prepare", PrepareScript(run, marker), TimeSpan.FromMinutes(10));
            evidence["prepare"] = prep;
            var fullIso = prep.GetProperty("fullIso").GetString()!;
            var minWim = prep.GetProperty("minWim").GetString()!;
            var linuxSource = prep.GetProperty("linuxSource").GetString()!;
            check(File.Exists(fullIso) && File.Exists(minWim) && File.Exists(linuxSource),
                $"Synthetic sources prepared: {prep.GetProperty("fullWimBytes").GetInt64():N0}-byte WIM in an IMAPI2 ISO, a WIM without boot files and a 1 GB VHDX");

            // 1. Windows: the full pipeline from an ISO, with kernel-debug settings and a tools folder.
            var started = await call("hyperv_image_build", new
            {
                osFamily = "windows", name = "HyperVControl-validation-win", outputDirectory = output, isoPath = fullIso, diskGb = 4,
                computerName = "HVC-VALID-01", adminPassword = password, toolsDirectory = tools, waitSeconds = 900,
                kernelDebug = new { enabled = true, transport = "net", hostIp = "192.0.2.10", port = 50005, key = kdKey }
            }, label: "image-build-windows");
            Redact(Path.Combine(results, "image-build-windows.json"), kdKey);
            var job = started.GetProperty("job");
            var result = job.GetProperty("result");
            var vhdx = result.GetProperty("vhdPath").GetString()!;
            var warnings = result.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();
            var boot = result.GetProperty("bootConfiguration").GetString();
            evidence["windowsBuild"] = new
            {
                status = job.GetProperty("status").GetString(), durationSeconds = result.GetProperty("durationSeconds").GetDouble(),
                vhdPath = vhdx, vhdFileBytes = result.GetProperty("vhdFileBytes").GetInt64(), bootConfiguration = boot,
                bootSummary = result.GetProperty("bootSummary").GetString(), kernelDebugApplied = result.GetProperty("kernelDebugApplied").GetBoolean(),
                toolsCopied = result.GetProperty("toolsCopied"), partitions = result.GetProperty("partitions"), warnings
            };
            check(started.GetProperty("ok").GetBoolean() && job.GetProperty("status").GetString() == "succeeded" && File.Exists(vhdx)
                && started.GetProperty("credentials").GetProperty("password").ValueKind == JsonValueKind.Null
                && started.GetProperty("kernelDebug").GetProperty("kernelAttachString").GetString() == $"net:port=50005,key={kdKey}",
                $"Windows build job succeeded in {result.GetProperty("durationSeconds").GetDouble():0.0} s ({boot}); a supplied password is not echoed; the KDNET attach string is returned at start");
            var sidecarText = File.ReadAllText(result.GetProperty("sidecarPath").GetString()!);
            using (var sidecar = JsonDocument.Parse(sidecarText))
                check(sidecar.RootElement.GetProperty("kind").GetString() == "template" && sidecar.RootElement.GetProperty("osFamily").GetString() == "windows"
                    && !sidecar.RootElement.GetProperty("config").GetProperty("secureBoot").GetBoolean()
                    && !sidecarText.Contains(password, StringComparison.Ordinal) && !sidecarText.Contains(kdKey, StringComparison.Ordinal),
                    "Sidecar written beside the VHDX (template, Secure Boot off for kernel debugging) without the password or KDNET key");

            var verify = await ElevatedAsync(run, "verify-windows", VerifyWindowsScript(run, vhdx), TimeSpan.FromMinutes(5));
            var partitions = verify.GetProperty("partitions").EnumerateArray().ToList();
            var bcdDefault = verify.GetProperty("enumDefault").GetString()!;
            var bcdDebug = verify.GetProperty("enumDbgsettings").GetString()!;
            var bcdManager = verify.GetProperty("enumBootmgr").GetString()!;
            // Read from a copy no bcdedit has opened, in this (unelevated) process: offreg.dll needs only read access.
            var storeFlags = ImageBuildService.ReadSystemStoreFlags(verify.GetProperty("bcdFlagsCopy").GetString()!);
            var bcdLogs = verify.GetProperty("bcdLogs").EnumerateArray().Select(l => l.GetString()).ToList();
            evidence["windowsVerification"] = new
            {
                partitionStyle = verify.GetProperty("partitionStyle").GetString(), partitions = verify.GetProperty("partitions"),
                espFiles = verify.GetProperty("espFiles"), marker = verify.GetProperty("marker").GetString() == "marker " + marker,
                enumBootmgr = bcdManager, enumDefault = bcdDefault, enumDbgsettings = bcdDebug.Replace(kdKey, "<redacted>", StringComparison.Ordinal),
                systemStore = new { storeFlags.KeyName, storeFlags.System, storeFlags.TreatAsSystem }, bcdLogs,
                tools = verify.GetProperty("tools"), bootx64Signer = verify.GetProperty("bootx64Signer").GetString()
            };
            check(verify.GetProperty("partitionStyle").GetString() == "GPT" && partitions.Count == 3
                && Partition(partitions[0], "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}", 300L << 20, "FAT32")
                && Partition(partitions[1], "{e3c9e316-0b5c-4db8-817d-f92df00215ae}", 16L << 20, "")
                && Partition(partitions[2], "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}", null, "NTFS"),
                "Read-only mount: GPT with a 300 MB FAT32 EFI system partition, a 16 MB MSR and an NTFS Windows partition");
            check(verify.GetProperty("marker").GetString() == "marker " + marker && verify.GetProperty("configFiles").GetInt32() >= 5,
                "The applied image contains the synthetic WIM's marker and Windows\\System32\\config placeholders");
            XNamespace u = "urn:schemas-microsoft-com:unattend";
            var unattend = XDocument.Parse(verify.GetProperty("unattend").GetString()!);
            check(unattend.Descendants(u + "ComputerName").Single().Value == "HVC-VALID-01" && unattend.Descendants(u + "LocalAccount").Single().Element(u + "Name")!.Value == "labadmin"
                && unattend.Descendants(u + "Password").All(p => p.Element(u + "Value")!.Value == password) && unattend.Descendants(u + "AutoLogon").Any()
                && unattend.Descendants(u + "fDenyTSConnections").Single().Value == "false" && verify.GetProperty("unattendHasBom").GetBoolean(),
                "Windows\\Panther\\unattend.xml in the image has the computer name, local admin, autologon, RDP and the exact password");
            var copied = verify.GetProperty("tools").EnumerateArray().ToDictionary(t => t.GetProperty("path").GetString()!, t => t.GetProperty("sha256").GetString()!);
            check(copied.Count == 3 && copied["readme.txt"] == Hash(Path.Combine(tools, "readme.txt")) && copied["bin\\\u7a7a \u95f4.dat"] == Hash(Path.Combine(tools, "bin", "\u7a7a \u95f4.dat"))
                && copied["hidden.cfg"] == Hash(Path.Combine(tools, "hidden.cfg")), "C:\\Tools in the image matches the tools folder byte for byte (Unicode and hidden files included)");
            var esp = verify.GetProperty("espFiles");
            check(esp.GetProperty("bootx64").GetBoolean() && esp.GetProperty("bootmgfw").GetBoolean() && esp.GetProperty("bcd").GetBoolean()
                && Regex.IsMatch(bcdManager, @"(?m)^default\s+\{default\}") && Regex.IsMatch(bcdDefault, @"(?m)^path\s+\\Windows\\system32\\winload\.efi")
                && Regex.IsMatch(bcdDefault, @"(?m)^debug\s+Yes") && Regex.IsMatch(bcdDebug, @"(?m)^debugtype\s+NET") && Regex.IsMatch(bcdDebug, @"(?m)^hostip\s+192\.0\.2\.10")
                && Regex.IsMatch(bcdDebug, @"(?m)^port\s+50005") && bcdDebug.Contains(kdKey, StringComparison.Ordinal),
                $"EFI partition has bootx64.efi, bootmgfw.efi and a BCD store whose boot manager starts winload.efi with debug on and KDNET settings ({boot})");
            check(storeFlags == ("BCD00000000", 1, 1) && bcdLogs.Count == 0,
                $"The BCD store is a system store after the offline kernel-debug edit: Description KeyName=BCD00000000, System=1, TreatAsSystem=1, and no BCD.LOG* file beside it ({boot})");
            if (boot != "bcdboot /offline" && boot != "bcdboot")
                check(warnings.Any(w => w.Contains("c0000035", StringComparison.Ordinal)), "bcdboot could not create the store on this host (c0000035); the reported fallback built it with bcdedit");

            // 2. Windows: a WIM without boot files must fail at bcdboot with its reason and leave nothing behind.
            var failed = await call("hyperv_image_build", new
            {
                osFamily = "windows", name = "HyperVControl-validation-noboot", outputDirectory = output, imagePath = minWim, diskGb = 4, waitSeconds = 600
            }, allowError: true, label: "image-build-windows-noboot");
            var failedJob = failed.GetProperty("job");
            var reason = failed.GetProperty("error").GetString() ?? "";
            evidence["windowsNegative"] = new { status = failedJob.GetProperty("status").GetString(), error = reason };
            check(!failed.GetProperty("ok").GetBoolean() && failedJob.GetProperty("status").GetString() == "failed" && reason.StartsWith("bcdboot failed (exit ", StringComparison.Ordinal)
                && !File.Exists(Path.Combine(output, "HyperVControl-validation-noboot.vhdx")) && !File.Exists(Path.Combine(output, "HyperVControl-validation-noboot.image.json")),
                "A WIM without boot files fails with bcdboot's own reason; its VHDX and sidecar are removed: " + reason);

            // 3. Linux: the qemu-img precondition fails before any download (the URL is unreachable by design).
            var qemuProbe = await call("hyperv_image_build", new
            {
                osFamily = "linux", name = "HyperVControl-validation-qemu", outputDirectory = output, imageUrl = "https://example.invalid/ubuntu.img", sshPublicKey = sshKey
            }, allowError: true, label: "image-build-linux-qemu");
            var qemuError = qemuProbe.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            var qemuPresent = qemuProbe.GetProperty("ok").GetBoolean() || !qemuError.Contains("HYPERV_CONTROL_QEMU_IMG", StringComparison.Ordinal);
            evidence["linuxQemu"] = new { qemuPresent, error = qemuError, errorType = qemuProbe.TryGetProperty("errorType", out var t) ? t.GetString() : null };
            if (qemuPresent) Console.WriteLine("NOTE qemu-img is available to the server; the QCOW2 conversion path was not exercised here (no download is made).");
            else check(qemuProbe.GetProperty("errorType").GetString() == "NotFound", "qemu-img is absent: the build is refused before any download, naming HYPERV_CONTROL_QEMU_IMG (QCOW2 conversion not exercised)");

            // 4. Linux: a .vhdx source (no conversion), grown with Resize-VHD, plus a real CIDATA seed ISO.
            var linux = await call("hyperv_image_build", new
            {
                osFamily = "linux", name = "HyperVControl-validation-lnx", outputDirectory = output, imagePath = linuxSource, diskGb = 4,
                sshPublicKey = sshKey, hostname = "hvc-validation", waitSeconds = 300
            }, label: "image-build-linux");
            var linuxResult = linux.GetProperty("job").GetProperty("result");
            var seed = linuxResult.GetProperty("seedIsoPath").GetString()!;
            var linuxVhdx = linuxResult.GetProperty("vhdPath").GetString()!;
            check(linux.GetProperty("ok").GetBoolean() && linux.GetProperty("job").GetProperty("status").GetString() == "succeeded" && File.Exists(seed) && File.Exists(linuxVhdx),
                "Linux build from a .vhdx succeeded: VHDX copied and a seed ISO written");
            var linuxVerify = await ElevatedAsync(run, "verify-linux", VerifyLinuxScript(linuxVhdx, seed), TimeSpan.FromMinutes(5));
            evidence["linuxVerification"] = linuxVerify;
            var userData = linuxVerify.GetProperty("userData").GetString()!;
            check(linuxVerify.GetProperty("size").GetInt64() == 4L << 30 && linuxVerify.GetProperty("vhdFormat").GetString() == "VHDX"
                && linuxVerify.GetProperty("label").GetString() == "CIDATA" && userData.StartsWith("#cloud-config\n", StringComparison.Ordinal) && !userData.Contains('\r')
                && userData.Contains(sshKey, StringComparison.Ordinal) && userData.Contains("linux-cloud-tools-virtual", StringComparison.Ordinal)
                && linuxVerify.GetProperty("metaData").GetString()!.Contains("local-hostname: \"hvc-validation\"", StringComparison.Ordinal)
                && linuxVerify.GetProperty("rootEntries").EnumerateArray().Select(n => n.GetString()).OrderBy(n => n).SequenceEqual(["meta-data", "user-data"]),
                "Resize-VHD grew the copy to 4 GB; Windows mounts the seed ISO as volume CIDATA with exactly user-data and meta-data (LF, key, KVP package, hostname)");
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try
            {
                var cleanup = await ElevatedAsync(run, "cleanup", CleanupScript(run), TimeSpan.FromMinutes(5), deleteRunAfter: true);
                evidence["cleanup"] = cleanup;
                if (failure is null)
                    check(cleanup.GetProperty("removed").GetBoolean() && cleanup.GetProperty("attachedAfter").GetInt32() == 0,
                        "Cleanup detached everything the test created and deleted the run directory");
            }
            catch (Exception ex) when (failure is not null) { evidence["cleanupError"] = ex.Message; }
            evidence["finishedAt"] = DateTimeOffset.UtcNow;
            evidence["failure"] = failure?.Message;
            await File.WriteAllTextAsync(Path.Combine(results, "image-build-live-evidence.json"),
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }).Replace(kdKey, "<redacted>", StringComparison.Ordinal).Replace(password, "<redacted>", StringComparison.Ordinal));
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
    }

    private static bool Partition(JsonElement p, string gptType, long? size, string fileSystem) =>
        p.GetProperty("gptType").GetString() == gptType && (size is null || p.GetProperty("size").GetInt64() == size) && p.GetProperty("fileSystem").GetString() == fileSystem;

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void Redact(string path, string secret)
    {
        if (File.Exists(path)) File.WriteAllText(path, File.ReadAllText(path).Replace(secret, "<redacted>", StringComparison.Ordinal));
    }

    private static string Q(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>Runs a Windows PowerShell 5.1 script elevated (runas) and returns the JSON it writes; the script never sees the harness's console.</summary>
    private static async Task<JsonElement> ElevatedAsync(string run, string name, string body, TimeSpan timeout, bool deleteRunAfter = false)
    {
        var helpers = deleteRunAfter ? Path.Combine(Path.GetTempPath(), "HyperVControl-imagebuild-live-" + Guid.NewGuid().ToString("N")[..8]) : Path.Combine(run, "helpers");
        Directory.CreateDirectory(helpers);
        var script = Path.Combine(helpers, name + ".ps1");
        var resultPath = Path.Combine(helpers, name + ".result.json");
        await File.WriteAllTextAsync(script, "$ErrorActionPreference = 'Stop'\n$ProgressPreference = 'SilentlyContinue'\n"
            + "$resultPath = " + Q(resultPath) + "\n"
            + "try {\n$out = & {\n" + body + "\n}\n$out | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding UTF8\n}\n"
            + "catch { @{ error = ($_ | Out-String) } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8 }\n", new UTF8Encoding(true));
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the elevated helper.");
            using var deadline = new CancellationTokenSource(timeout);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException($"Elevated helper {name} exceeded {timeout.TotalMinutes} minutes."); }
            if (!File.Exists(resultPath)) throw new InvalidOperationException($"Elevated helper {name} wrote no result (exit {process.ExitCode}).");
            var json = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath)).RootElement.Clone();
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var error)) throw new InvalidOperationException($"Elevated helper {name} failed: {error.GetString()}");
            return json;
        }
        finally
        {
            if (deleteRunAfter) try { Directory.Delete(helpers, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private const string IsoWriter = """
        if (-not ('HvcLiveIsoWriter' -as [type])) {
        Add-Type -TypeDefinition @'
        public static class HvcLiveIsoWriter {
            public static void Save(object comStream, string path) {
                var stream = (System.Runtime.InteropServices.ComTypes.IStream)comStream;
                var buffer = new byte[1048576];
                var read = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
                try {
                    using (var file = new System.IO.FileStream(path, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write)) {
                        while (true) {
                            stream.Read(buffer, buffer.Length, read);
                            var count = System.Runtime.InteropServices.Marshal.ReadInt32(read);
                            if (count <= 0) break;
                            file.Write(buffer, 0, count);
                        }
                    }
                } finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(read); }
            }
        }
        '@
        }
        """;

    /// <summary>Synthetic sources: a full WIM (placeholders plus the host's boot files, BCD template and bootstr.dll, read-only copies) in an ISO, a WIM without boot files, and a 1 GB VHDX.</summary>
    private static string PrepareScript(string run, string marker) => IsoWriter + "\n" + $$"""
        $run = {{Q(run)}}
        $full = Join-Path $run 'content-full'
        $min = Join-Path $run 'content-min'
        foreach ($root in $full, $min) {
            New-Item -ItemType Directory -Force (Join-Path $root 'Windows\System32\config') | Out-Null
            foreach ($hive in 'SYSTEM', 'SOFTWARE', 'SAM', 'SECURITY', 'DEFAULT') { Set-Content -LiteralPath (Join-Path $root "Windows\System32\config\$hive") -Value "placeholder $hive" -Encoding ASCII }
            Set-Content -LiteralPath (Join-Path $root 'Windows\HyperVControl-synthetic.txt') -Value {{Q("marker " + marker)}} -Encoding ASCII -NoNewline
        }
        Copy-Item -LiteralPath "$env:SystemRoot\System32\config\BCD-Template" -Destination (Join-Path $full 'Windows\System32\config\BCD-Template')
        Copy-Item -LiteralPath "$env:SystemRoot\System32\bootstr.dll" -Destination (Join-Path $full 'Windows\System32\bootstr.dll')
        New-Item -ItemType Directory -Force (Join-Path $full 'Windows\System32\en-US'), (Join-Path $full 'Windows\Boot') | Out-Null
        Copy-Item -LiteralPath "$env:SystemRoot\System32\en-US\bootstr.dll.mui" -Destination (Join-Path $full 'Windows\System32\en-US\bootstr.dll.mui')
        foreach ($folder in 'EFI', 'Fonts', 'Resources') { Copy-Item -LiteralPath "$env:SystemRoot\Boot\$folder" -Destination (Join-Path $full "Windows\Boot\$folder") -Recurse }
        $fullWim = Join-Path $run 'full.wim'
        $minWim = Join-Path $run 'min.wim'
        New-WindowsImage -CapturePath $full -ImagePath $fullWim -Name 'HyperVControl Synthetic' -CompressionType Fast | Out-Null
        New-WindowsImage -CapturePath $min -ImagePath $minWim -Name 'HyperVControl Synthetic Without Boot Files' -CompressionType Fast | Out-Null
        $isoContent = Join-Path $run 'iso-content'
        New-Item -ItemType Directory -Force (Join-Path $isoContent 'sources') | Out-Null
        Copy-Item -LiteralPath $fullWim -Destination (Join-Path $isoContent 'sources\install.wim')
        $fullIso = Join-Path $run 'synthetic.iso'
        $fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
        $fsi.FileSystemsToCreate = 3
        $fsi.VolumeName = 'HVC_SYNTHETIC'
        $fsi.Root.AddTree($isoContent, $false)
        [HvcLiveIsoWriter]::Save($fsi.CreateResultImage().ImageStream, $fullIso)
        $linuxSource = Join-Path $run 'linux-source.vhdx'
        New-VHD -Path $linuxSource -SizeBytes 1GB -Dynamic | Out-Null
        @{ fullIso = $fullIso; minWim = $minWim; linuxSource = $linuxSource; fullWimBytes = [long](Get-Item -LiteralPath $fullWim).Length; isoBytes = [long](Get-Item -LiteralPath $fullIso).Length
           hostBuild = [string](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuild + '.' + [string](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').UBR }
        """;

    /// <summary>Mounts the built VHDX read-only without drive letters and reads partitions, files, the unattend and a copy of the BCD store.</summary>
    private static string VerifyWindowsScript(string run, string vhdx) => $$"""
        $vhdx = {{Q(vhdx)}}
        $bcdCopy = Join-Path {{Q(run)}} 'bcd-copy'
        $mounted = Mount-VHD -Path $vhdx -ReadOnly -NoDriveLetter -Passthru
        try {
            $number = [int]$mounted.DiskNumber
            Start-Sleep -Seconds 2
            $parts = @(Get-Partition -DiskNumber $number | Sort-Object PartitionNumber)
            $rows = @(foreach ($p in $parts) {
                $volume = Get-Volume -Partition $p -ErrorAction SilentlyContinue
                @{ number = [int]$p.PartitionNumber; gptType = [string]$p.GptType; size = [long]$p.Size; fileSystem = [string]$volume.FileSystem; label = [string]$volume.FileSystemLabel; path = [string]$volume.Path }
            })
            $espPath = ($rows | Where-Object { $_.gptType -eq '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' } | Select-Object -First 1).path
            $winPath = ($rows | Where-Object { $_.fileSystem -eq 'NTFS' } | Select-Object -First 1).path
            $unattendBytes = [IO.File]::ReadAllBytes($winPath + 'Windows\Panther\unattend.xml')
            $toolsRoot = $winPath + 'Tools\'
            $tools = @(Get-ChildItem -LiteralPath $toolsRoot -Recurse -File -Force | ForEach-Object {
                $stream = [IO.File]::OpenRead($_.FullName)
                try { $hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)).Replace('-', '') } finally { $stream.Dispose() }
                @{ path = [string]$_.FullName.Substring($toolsRoot.Length); sha256 = $hash; bytes = [long]$_.Length }
            })
            [IO.File]::Copy($espPath + 'EFI\Microsoft\Boot\BCD', $bcdCopy, $true)
            # A second copy that bcdedit never opens (bcdedit /store clears TreatAsSystem when it closes a store), for the system-store flags.
            $bcdFlagsCopy = Join-Path {{Q(run)}} 'bcd-flags-copy'
            [IO.File]::Copy($espPath + 'EFI\Microsoft\Boot\BCD', $bcdFlagsCopy, $true)
            $bcdLogs = @([IO.Directory]::GetFiles($espPath + 'EFI\Microsoft\Boot', 'BCD.LOG*') | ForEach-Object { [IO.Path]::GetFileName($_) })
            $bootx64 = Join-Path {{Q(run)}} 'bootx64-copy.efi'
            [IO.File]::Copy($espPath + 'EFI\Boot\bootx64.efi', $bootx64, $true)
            $bcdedit = Join-Path $env:SystemRoot 'System32\bcdedit.exe'
            function Read-Bcd([string[]]$Arguments) { $ErrorActionPreference = 'Continue'; ((& $bcdedit /store $bcdCopy @Arguments 2>&1) | ForEach-Object { [string]$_ }) -join "`n" }
            @{
                partitionStyle = [string](Get-Disk -Number $number).PartitionStyle
                partitions = $rows
                espFiles = @{ bootx64 = [IO.File]::Exists($espPath + 'EFI\Boot\bootx64.efi'); bootmgfw = [IO.File]::Exists($espPath + 'EFI\Microsoft\Boot\bootmgfw.efi'); bcd = [IO.File]::Exists($espPath + 'EFI\Microsoft\Boot\BCD') }
                bcdFlagsCopy = $bcdFlagsCopy
                bcdLogs = $bcdLogs
                marker = [IO.File]::ReadAllText($winPath + 'Windows\HyperVControl-synthetic.txt')
                configFiles = @(Get-ChildItem -LiteralPath ($winPath + 'Windows\System32\config') -File).Count
                unattendHasBom = ($unattendBytes.Length -ge 3 -and $unattendBytes[0] -eq 0xEF -and $unattendBytes[1] -eq 0xBB -and $unattendBytes[2] -eq 0xBF)
                unattend = [Text.Encoding]::UTF8.GetString($unattendBytes).TrimStart([char]0xFEFF)
                tools = $tools
                enumBootmgr = Read-Bcd @('/enum', '{bootmgr}')
                enumDefault = Read-Bcd @('/enum', '{default}')
                enumDbgsettings = Read-Bcd @('/enum', '{dbgsettings}')
                bootx64Signer = [string](Get-AuthenticodeSignature -LiteralPath $bootx64).SignerCertificate.Issuer
            }
        } finally { Dismount-VHD -Path $vhdx }
        """;

    private static string VerifyLinuxScript(string vhdx, string seed) => $$"""
        $vhd = Get-VHD -Path {{Q(vhdx)}}
        $iso = {{Q(seed)}}
        $image = Mount-DiskImage -ImagePath $iso -StorageType ISO -Access ReadOnly -PassThru
        try {
            $letter = $null
            for ($i = 0; $i -lt 40 -and -not $letter; $i++) { $volume = Get-Volume -DiskImage $image; if ([string]$volume.DriveLetter -match '^[A-Za-z]$') { $letter = [string]$volume.DriveLetter } else { Start-Sleep -Milliseconds 250 } }
            $root = $letter + ':\'
            @{
                size = [long]$vhd.Size; vhdFormat = [string]$vhd.VhdFormat; vhdType = [string]$vhd.VhdType
                label = [string]$volume.FileSystemLabel; fileSystem = [string]$volume.FileSystem
                rootEntries = @(Get-ChildItem -LiteralPath $root -Force | ForEach-Object { [string]$_.Name })
                userData = [IO.File]::ReadAllText($root + 'user-data'); metaData = [IO.File]::ReadAllText($root + 'meta-data')
            }
        } finally { Dismount-DiskImage -ImagePath $iso | Out-Null }
        """;

    /// <summary>Detaches every VHDX and ISO under the run directory, deletes it, and reports what is still attached.</summary>
    private static string CleanupScript(string run) => $$"""
        $run = {{Q(run)}}
        $detached = @()
        if (Test-Path -LiteralPath $run) {
            foreach ($file in @(Get-ChildItem -LiteralPath $run -Recurse -File -Force | Where-Object { $_.Extension -in '.vhdx', '.iso' })) {
                if ($file.Extension -eq '.vhdx') { $vhd = Get-VHD -Path $file.FullName -ErrorAction SilentlyContinue; if ($vhd -and $vhd.Attached) { Dismount-VHD -Path $file.FullName; $detached += [string]$file.FullName } }
                else { $image = Get-DiskImage -ImagePath $file.FullName -ErrorAction SilentlyContinue; if ($image -and $image.Attached) { Dismount-DiskImage -ImagePath $file.FullName | Out-Null; $detached += [string]$file.FullName } }
            }
            Remove-Item -LiteralPath $run -Recurse -Force
        }
        $attached = @(Get-Disk | Where-Object { [string]$_.Location -like ($run + '*') })
        @{ detached = $detached; removed = -not (Test-Path -LiteralPath $run); attachedAfter = $attached.Count }
        """;
}
