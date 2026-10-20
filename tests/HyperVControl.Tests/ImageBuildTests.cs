using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using HyperVControl.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HyperVControl.Tests;

/// <summary>
/// Golden image builds against the recording mock: input validation, unattend and NoCloud content, script rendering and
/// ordering for both pipelines, cleanup on failure, resumable downloads (loopback HTTP), the sidecar and the tool contract.
/// The IMAPI2 seed ISO is built for real (inbox COM, no administrator rights needed).
/// </summary>
internal static class ImageBuildTests
{
    private const string Store = @"S:\EFI\Microsoft\Boot\BCD";
    private const string Password = "p<&>\"w0rd'!";
    private const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHyperVControlTestKeyOnlyForUnitTests lab@\"host\"";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var roots = new List<string>();
        string Temp(string label)
        {
            var directory = Path.Combine(Path.GetTempPath(), "HyperVControl-imagebuild-" + label + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(directory);
            roots.Add(directory);
            return directory;
        }
        try
        {
            CloudInitAndCrypt(check);
            Unattend(check);
            ScriptRendering(check);
            SystemStore(check, Temp);
            await Downloads(check, Temp);
            await Validation(check, Temp);
            await WindowsPipeline(check, Temp);
            await LinuxPipeline(check, Temp);
            await ToolContract(check, Temp);
        }
        finally
        {
            foreach (var root in roots)
                try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void CloudInitAndCrypt(Action<bool, string> check)
    {
        check(ImageBuildCloudInit.Sha512Crypt("Hello world!", "saltstring") == "$6$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1"
            && ImageBuildCloudInit.Sha512Crypt("p<&>\"w0rd! $x", "Ab1/.xyz") == "$6$Ab1/.xyz$/wvQYkrJKc9FgzPAlOVXsfyUmx3RPSB50f7eD6ZBWLHcisbdOBuKpUICcvvdWJwNhFJKmdI4IpCc7kwBL94CT0",
            "SHA-512-crypt matches Drepper's published vector and an OpenSSL 3.5 reference");
        var first = ImageBuildCloudInit.Sha512Crypt("same");
        check(first.StartsWith("$6$", StringComparison.Ordinal) && first.Split('$')[2].Length == 16 && first != ImageBuildCloudInit.Sha512Crypt("same"),
            "Linux passwords get a random 16-character salt");

        var userData = ImageBuildCloudInit.UserData("lab", [Key], null, "lab-ubuntu", ["linux-cloud-tools-virtual"]);
        check(userData == "#cloud-config\nhostname: \"lab-ubuntu\"\nusers:\n  - name: \"lab\"\n    groups: [sudo]\n    shell: /bin/bash\n    sudo: \"ALL=(ALL) NOPASSWD:ALL\"\n"
            + "    lock_passwd: true\n    ssh_authorized_keys:\n      - \"ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHyperVControlTestKeyOnlyForUnitTests lab@\\\"host\\\"\"\n"
            + "ssh_pwauth: false\npackage_update: true\npackages:\n  - \"linux-cloud-tools-virtual\"\n",
            "user-data: cloud-config with the SSH user, quoted key, KVP package, LF line endings");
        var hash = ImageBuildCloudInit.Sha512Crypt("secret", "abcdefgh");
        var passwordOnly = ImageBuildCloudInit.UserData("lab", [], hash, "h1", []);
        check(passwordOnly.Contains("    lock_passwd: false\n    passwd: \"" + hash + "\"\n", StringComparison.Ordinal) && passwordOnly.Contains("ssh_pwauth: true\n", StringComparison.Ordinal)
            && !passwordOnly.Contains("secret", StringComparison.Ordinal) && !passwordOnly.Contains("packages", StringComparison.Ordinal),
            "Password-only user-data carries a SHA-512-crypt hash, enables SSH passwords and omits empty package lists");
        check(ImageBuildCloudInit.MetaData("iid-123", "lab-ubuntu") == "instance-id: \"iid-123\"\nlocal-hostname: \"lab-ubuntu\"\n", "meta-data carries instance-id and local-hostname");
    }

    private static void Unattend(Action<bool, string> check)
    {
        XNamespace u = ImageBuildUnattend.Namespace, wcm = ImageBuildUnattend.WcmNamespace;
        var xml = ImageBuildUnattend.Build(new UnattendSettings("HVC-&<LAB>", "labadmin", Password, true, true, true, "en-US", "de-DE", "en-US", "fr-FR"));
        var doc = XDocument.Parse(xml);
        var settings = doc.Root!.Elements(u + "settings").ToList();
        check(doc.Root.Name == u + "unattend" && settings.Select(s => (string?)s.Attribute("pass")).SequenceEqual(["specialize", "oobeSystem"])
            && doc.Descendants(u + "component").All(c => (string?)c.Attribute("processorArchitecture") == "amd64" && (string?)c.Attribute("publicKeyToken") == "31bf3856ad364e35"),
            "Unattend has only specialize and oobeSystem passes with amd64 components");
        var passwords = doc.Descendants(u + "Password").Select(p => (p.Element(u + "Value")?.Value, p.Element(u + "PlainText")?.Value)).ToList();
        check(xml.Contains("&lt;", StringComparison.Ordinal) && xml.Contains("&amp;", StringComparison.Ordinal) && !xml.Contains(Password, StringComparison.Ordinal)
            && doc.Descendants(u + "ComputerName").Single().Value == "HVC-&<LAB>" && passwords.Count == 2 && passwords.All(p => p == (Password, "true")),
            "Password and computer name are XML-escaped and round-trip exactly (account and autologon)");
        var specialize = settings[0];
        var commands = specialize.Descendants(u + "RunSynchronousCommand").Select(c => (c.Element(u + "Order")!.Value, c.Element(u + "Path")!.Value)).ToList();
        var firewall = specialize.Descendants(u + "FirewallGroup").Single();
        check(specialize.Descendants(u + "fDenyTSConnections").Single().Value == "false"
            && (string?)firewall.Attribute(wcm + "action") == "add" && (string?)firewall.Attribute(wcm + "keyValue") == "RemoteDesktop"
            && firewall.Element(u + "Group")!.Value == "@FirewallAPI.dll,-28752" && firewall.Element(u + "Profile")!.Value == "all"
            && commands.Count == 2 && commands[0].Item1 == "1" && commands[0].Item2.Contains("PreventDeviceEncryption /t REG_DWORD /d 1", StringComparison.Ordinal)
            && commands[1].Item1 == "2" && commands[1].Item2.Contains("BypassNRO", StringComparison.Ordinal),
            "specialize enables RDP and its firewall group, prevents device encryption and sets BypassNRO");
        var oobe = settings[1];
        var account = oobe.Descendants(u + "LocalAccount").Single();
        var autoLogon = oobe.Descendants(u + "AutoLogon").Single();
        var firstLogon = oobe.Descendants(u + "SynchronousCommand").Select(c => c.Element(u + "CommandLine")!.Value).ToList();
        check(oobe.Descendants(u + "InputLocale").Single().Value == "en-US" && oobe.Descendants(u + "SystemLocale").Single().Value == "de-DE"
            && oobe.Descendants(u + "UserLocale").Single().Value == "fr-FR" && oobe.Descendants(u + "HideOnlineAccountScreens").Single().Value == "true"
            && oobe.Descendants(u + "ProtectYourPC").Single().Value == "3" && account.Element(u + "Name")!.Value == "labadmin" && account.Element(u + "Group")!.Value == "Administrators"
            && autoLogon.Element(u + "Enabled")!.Value == "true" && autoLogon.Element(u + "Username")!.Value == "labadmin" && autoLogon.Element(u + "LogonCount")!.Value == "9999999"
            && firstLogon.Count == 3 && firstLogon[0].Contains("DevicePasswordLessBuildVersion /t REG_DWORD /d 0", StringComparison.Ordinal)
            && firstLogon[1] == "net.exe accounts /maxpwage:unlimited" && firstLogon[2] == "cmd.exe /c del /q %WINDIR%\\Panther\\unattend.xml",
            "oobeSystem sets locales, hides OOBE pages, creates the local admin with autologon and deletes the answer file at first logon");
        var minimal = XDocument.Parse(ImageBuildUnattend.Build(new UnattendSettings("LAB", "admin", "x", false, false, false, "en-US", "en-US", "en-US", "en-US")));
        check(!minimal.Descendants(u + "fDenyTSConnections").Any() && !minimal.Descendants(u + "FirewallGroups").Any() && !minimal.Descendants(u + "AutoLogon").Any()
            && minimal.Descendants(u + "RunSynchronousCommand").Select(c => c.Element(u + "Path")!.Value).Single().Contains("BypassNRO", StringComparison.Ordinal),
            "enableRdp, autoLogon and preventDeviceEncryption set to false remove their unattend settings");
    }

    private static void ScriptRendering(Action<bool, string> check)
    {
        var disk = ImageBuildScripts.CreateDisk(@"D:\Images\lab'1.vhdx", 64).Body;
        int At(string text) => disk.IndexOf(text, StringComparison.Ordinal);
        int[] order = [At("New-VHD -Path 'D:\\Images\\lab''1.vhdx' -SizeBytes ([long]64*1GB) -Dynamic:$true"), At("Mount-VHD -Path 'D:\\Images\\lab''1.vhdx' -Passthru:$true"),
            At("Initialize-Disk -Number $diskNumber -PartitionStyle 'GPT'"), At("Remove-Partition -DiskNumber $diskNumber"),
            At("$system = New-Partition -DiskNumber $diskNumber -Size ([long]300*1MB) -GptType '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'"),
            At("Format-Volume -Partition $system -FileSystem 'FAT32' -NewFileSystemLabel 'System' -Force:$true -Confirm:$false"),
            At("Set-Partition -DiskNumber $diskNumber -PartitionNumber $system.PartitionNumber -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'"),
            At("Add-PartitionAccessPath -DiskNumber $diskNumber -PartitionNumber $system.PartitionNumber -AssignDriveLetter:$true"),
            At("New-Partition -DiskNumber $diskNumber -Size ([long]16*1MB) -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}'"),
            At("$windows = New-Partition -DiskNumber $diskNumber -UseMaximumSize:$true -GptType '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'"),
            At("Format-Volume -Partition $windows -FileSystem 'NTFS' -NewFileSystemLabel 'Windows'"),
            At("Add-PartitionAccessPath -DiskNumber $diskNumber -PartitionNumber $windows.PartitionNumber -AssignDriveLetter:$true"),
            At("$systemLetter = [string](Get-Partition")];
        check(order.All(i => i >= 0) && order.Zip(order.Skip(1)).All(p => p.First < p.Second)
            && disk.Contains("if ([string]::IsNullOrWhiteSpace($env:HYPERV_CONTROL_MODULE_PATH)) {", StringComparison.Ordinal)
            && disk.Contains("'Storage', 'Storage.psd1'", StringComparison.Ordinal) && !disk.Contains("'Dism', 'Dism.psd1'", StringComparison.Ordinal),
            "Disk script: GPT, MSR removed, 300 MB ESP formatted FAT32 then retyped, 16 MB MSR, NTFS Windows; letters read back; Storage imported by path outside mock mode");
        var apply = ImageBuildScripts.ApplyImage(@"M:\sources\install.esd", 3, 'W').Body;
        check(apply.Contains("Expand-WindowsImage -ImagePath 'M:\\sources\\install.esd' -Index 3 -ApplyPath 'W:\\'", StringComparison.Ordinal)
            && apply.Contains("'Dism', 'Dism.psd1'", StringComparison.Ordinal), "Apply script: Expand-WindowsImage with the selected index; DISM imported by path");
        var boot = ImageBuildScripts.BcdBoot('W', 'S').Body;
        check(boot.Contains("$arguments = @('W:\\Windows', '/s', 'S:', '/f', 'UEFI', '/v')", StringComparison.Ordinal) && boot.Contains("if ($offline) { $arguments += '/offline' }", StringComparison.Ordinal)
            && boot.Contains("function Invoke-ImageBuildNative", StringComparison.Ordinal), "bcdboot script: /s system partition (no NVRAM entry), UEFI, /offline when supported");
        var net = ImageBuildScripts.BcdDebug('S', new ImageBuildKernelDebug(true, "net", "192.0.2.10", 50005, "1a.2b.3c.4d")).Body;
        var serial = ImageBuildScripts.BcdDebug('S', new ImageBuildKernelDebug(true, "serial", null, 50000, null, 2)).Body;
        check(net.Contains("$store = 'S:\\EFI\\Microsoft\\Boot\\BCD'", StringComparison.Ordinal)
            && net.Contains("Invoke-ImageBuildBcd 'debug on' @('/set', '{default}', 'debug', 'on')", StringComparison.Ordinal)
            && net.Contains("Invoke-ImageBuildBcd 'dbgsettings net' @('/dbgsettings', 'net', 'hostip:192.0.2.10', 'port:50005', 'key:1a.2b.3c.4d')", StringComparison.Ordinal)
            && serial.Contains("@('/dbgsettings', 'serial', 'debugport:2', 'baudrate:115200')", StringComparison.Ordinal),
            "Kernel-debug script: offline bcdedit /store debug on plus net or serial dbgsettings; failures name the step, not the key");
        var steps = ImageBuildScripts.BcdStoreSteps('W', 'S', "en-US", Guid.Empty, Guid.Empty);
        check(steps.Count == 31 && steps.Any(s => s.Arguments.SequenceEqual(["/create", "{00000000-0000-0000-0000-000000000000}", "/d", "Windows", "/application", "osloader"]))
            && steps.Any(s => s.Arguments.SequenceEqual(["/set", "{00000000-0000-0000-0000-000000000000}", "path", "\\Windows\\system32\\winload.efi"]))
            && steps.Any(s => s.Arguments.SequenceEqual(["/set", "{bootmgr}", "device", "partition=S:"])),
            "Fallback BCD store steps create the Windows Boot Loader, its resume object and the boot manager entries");
        var convert = ImageBuildScripts.QemuConvert(@"C:\q\qemu-img.exe", @"D:\c\ubuntu.img", "qcow2", @"D:\o\lab.vhdx").Body;
        check(convert.Contains("-ArgumentList @('convert', '-f', 'qcow2', '-O', 'vhdx', '-o', 'subformat=dynamic', 'D:\\c\\ubuntu.img', 'D:\\o\\lab.vhdx')", StringComparison.Ordinal),
            "qemu-img converts with an explicit source format to a dynamic VHDX");
        var iso = ImageBuildScripts.BuildIso(@"C:\seed dir", "CIDATA", @"D:\o\lab-seed.iso").Body;
        check(iso.Contains("IMAPI2FS.MsftFileSystemImage", StringComparison.Ordinal) && iso.Contains("$fsi.FileSystemsToCreate = 3", StringComparison.Ordinal)
            && iso.Contains("$fsi.VolumeName = 'CIDATA'", StringComparison.Ordinal) && iso.Contains("System.IO.FileMode.CreateNew", StringComparison.Ordinal),
            "Seed ISO script: IMAPI2 ISO 9660 + Joliet, label CIDATA, never overwrites");
    }

    /// <summary>System-store marking on real hive files, written and read with the inbox offline registry library (offreg.dll).</summary>
    private static void SystemStore(Action<bool, string> check, Func<string, string> temp)
    {
        var boot = Directory.CreateDirectory(Path.Combine(temp("bcd"), "EFI", "Microsoft", "Boot")).FullName;
        var store = Path.Combine(boot, "BCD");
        ImageBuildService.CreateEmptyHive(store);
        var header = File.ReadAllBytes(store);
        check(header.Length >= 4096 && header.AsSpan(0, 4).SequenceEqual("regf"u8) && ImageBuildService.ReadSystemStoreFlags(store) == ("", -1, -1),
            "An empty hive (a stand-in for BCD-Template) has a regf base block and no Description KeyName, System or TreatAsSystem value");

        foreach (var name in new[] { "BCD.LOG", "BCD.LOG1", "BCD.LOG2", "BCD.tmp" }) File.WriteAllBytes(Path.Combine(boot, name), new byte[512]);
        File.WriteAllBytes(Path.Combine(boot, "bootmgfw.efi"), [1]);
        File.SetAttributes(Path.Combine(boot, "BCD.LOG1"), FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly);
        File.SetAttributes(store, FileAttributes.Hidden | FileAttributes.System);
        ImageBuildService.MarkSystemStore(store);
        var marked = ImageBuildService.ReadSystemStoreFlags(store);
        var survivors = Directory.GetFiles(boot).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToList();
        var attributes = File.GetAttributes(store);
        ImageBuildService.MarkSystemStore(store);
        check(marked == ("BCD00000000", 1, 1) && ImageBuildService.ReadSystemStoreFlags(store) == marked && File.ReadAllBytes(store).AsSpan(0, 4).SequenceEqual("regf"u8)
            && survivors.SequenceEqual(["BCD", "bootmgfw.efi"]) && attributes == (FileAttributes.Hidden | FileAttributes.System),
            "MarkSystemStore sets Description KeyName=BCD00000000, System=1 and TreatAsSystem=1, keeps the store's attributes, deletes its BCD.LOG* logs and a stale BCD.tmp, and is idempotent");

        var notHive = Path.Combine(boot, "BCD-Template");
        File.WriteAllText(notHive, "TEMPLATE");
        var refused = Assert(() => ImageBuildService.MarkSystemStore(notHive));
        check(refused?.Message.Contains("not a registry hive", StringComparison.Ordinal) == true && File.ReadAllText(notHive) == "TEMPLATE",
            "MarkSystemStore refuses a file without a regf base block and leaves it unchanged");
    }

    private static async Task Downloads(Action<bool, string> check, Func<string, string> temp)
    {
        using var server = new LoopbackHttpServer();
        var payload = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 123);
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        server.Add("/images/disk.img", payload, "\"v1\"");
        server.Add("/images/SHA256SUMS", Encoding.ASCII.GetBytes($"{hash} *disk.img\n{new string('a', 64)}  other.img\n"));
        server.Add("/other/data.bin", Encoding.ASCII.GetBytes("unexpected content"));
        server.Redirect("/fwlink?id=1", "/images/disk.img");
        var client = ImageBuildDownloader.CreateClient();
        var downloader = new ImageBuildDownloader(client) { RetryDelay = TimeSpan.FromMilliseconds(50) };
        var url = new Uri(server.Base, "images/disk.img");

        check(await downloader.PublishedSha256Async(url, default) == hash
            && ImageBuildDownloader.ParseSha256Sums($"{hash}  disk.img\n", "disk.img") == hash && ImageBuildDownloader.ParseSha256Sums($"{hash} *disk.img", "other.img") is null,
            "SHA256SUMS next to the image is parsed for the exact file name (binary and text forms)");

        var cache = temp("cache");
        server.FailOnce("/images/disk.img", 1_000_000);
        var downloaded = await downloader.DownloadAsync(url, cache, hash, null, "test", default);
        check(downloaded.Sha256 == hash && File.ReadAllBytes(downloaded.Path).SequenceEqual(payload) && !downloaded.Reused
            && server.Requests.Any(r => r.Method == "GET" && r.Range is { } range && range.StartsWith("bytes=", StringComparison.Ordinal) && !range.StartsWith("bytes=0-", StringComparison.Ordinal))
            && !File.Exists(downloaded.Path + ".partial"),
            "An interrupted download resumes with an HTTP range request and is verified against SHA-256");
        var gets = server.Requests.Count(r => r.Method == "GET");
        var reused = await downloader.DownloadAsync(url, cache, hash, null, "test", default);
        check(reused.Reused && reused.Path == downloaded.Path && server.Requests.Count(r => r.Method == "GET") == gets, "A verified cached download is reused without transferring again");

        var badCache = temp("bad");
        var mismatch = await Expect<InvalidDataException>(() => downloader.DownloadAsync(new Uri(server.Base, "other/data.bin"), badCache, new string('0', 64), null, "test", default));
        check(mismatch is not null && Directory.GetFiles(badCache).Length == 0, "A SHA-256 mismatch fails the download and deletes it");

        var redirected = await downloader.DownloadAsync(new Uri(server.Base, "fwlink?id=1"), temp("redirect"), null, null, "test", default);
        check(Path.GetFileName(redirected.Path) == "disk.img" && redirected.FinalUrl.AbsolutePath == "/images/disk.img" && redirected.Sha256 == hash,
            "A redirecting link caches the file under the final URL's name and records its SHA-256");
    }

    private static async Task Validation(Action<bool, string> check, Func<string, string> temp)
    {
        using var mock = new MockHyperV(new { vms = Array.Empty<object>(), switches = Array.Empty<object>() });
        using var service = new ControlService();
        var build = service.ImageBuild;
        var root = temp("validation");
        var output = Path.Combine(root, "out");
        var iso = Path.Combine(root, "win.iso");
        File.WriteAllBytes(iso, [1]);
        var wim = Path.Combine(root, "install.wim");
        File.WriteAllBytes(wim, [1]);
        var image = Path.Combine(root, "ubuntu.img");
        File.WriteAllBytes(image, [1]);
        ImageBuildRequest Win(Func<ImageBuildRequest, ImageBuildRequest>? change = null) =>
            (change ?? (r => r))(new ImageBuildRequest { OsFamily = "windows", Name = "lab", OutputDirectory = output, IsoPath = iso });
        ImageBuildRequest Lin(Func<ImageBuildRequest, ImageBuildRequest>? change = null) =>
            (change ?? (r => r))(new ImageBuildRequest { OsFamily = "linux", Name = "lab", OutputDirectory = output, ImagePath = Path.ChangeExtension(image, ".vhdx"), SshPublicKey = Key });
        File.WriteAllBytes(Path.ChangeExtension(image, ".vhdx"), [1]);

        (string Case, ImageBuildRequest Request, string Expected)[] invalid =
        [
            ("os family", Win(r => r with { OsFamily = "macos" }), "osFamily must be"),
            ("path-like name", Win(r => r with { Name = "..\\evil" }), "name must be"),
            ("device name", Win(r => r with { Name = "CON" }), "reserved Windows device name"),
            ("relative output", Win(r => r with { OutputDirectory = "images" }), "absolute local path"),
            ("dot-dot output", Win(r => r with { OutputDirectory = root + "\\..\\x" }), "absolute local path"),
            ("UNC output", Win(r => r with { OutputDirectory = @"\\server\share\images" }), "absolute local path"),
            ("no windows source", Win(r => r with { IsoPath = null }), "exactly one Windows source"),
            ("two windows sources", Win(r => r with { Source = "eval" }), "exactly one Windows source"),
            ("iso extension", Win(r => r with { IsoPath = wim }), "must be a .iso file"),
            ("missing iso", Win(r => r with { IsoPath = Path.Combine(root, "missing.iso") }), "does not exist"),
            ("unknown source", Win(r => r with { IsoPath = null, Source = "retail" }), "source must be eval"),
            ("image index", Win(r => r with { ImageIndex = 0 }), "imageIndex must be"),
            ("index and edition", Win(r => r with { ImageIndex = 1, Edition = "Pro" }), "not both"),
            ("long computer name", Win(r => r with { ComputerName = "THIS-NAME-IS-TOO-LONG" }), "computerName must be"),
            ("numeric computer name", Win(r => r with { ComputerName = "12345" }), "computerName must be"),
            ("built-in admin", Win(r => r with { AdminUser = "Administrator" }), "built-in Windows account"),
            ("blank password", Win(r => r with { AdminPassword = "   " }), "printable characters"),
            ("control character password", Win(r => r with { AdminPassword = "a\nb" }), "printable characters"),
            ("kdnet without host", Win(r => r with { KernelDebug = new(true) }), "kernelDebug.hostIp"),
            ("kdnet port", Win(r => r with { KernelDebug = new(true, "net", "192.0.2.1", 80) }), "kernelDebug.port"),
            ("kdnet key", Win(r => r with { KernelDebug = new(true, "net", "192.0.2.1", 50000, "not-a-key") }), "kernelDebug.key"),
            ("serial port", Win(r => r with { KernelDebug = new(true, "serial", null, 50000, null, 3) }), "kernelDebug.comPort"),
            ("transport", Win(r => r with { KernelDebug = new(true, "usb") }), "kernelDebug.transport"),
            ("relative tools", Win(r => r with { ToolsDirectory = "tools" }), "absolute local path"),
            ("missing tools", Win(r => r with { ToolsDirectory = Path.Combine(root, "no-tools") }), "does not exist"),
            ("drive root tools", Win(r => r with { ToolsDirectory = Path.GetPathRoot(root) }), "not a drive root"),
            ("tools containing output", Win(r => r with { ToolsDirectory = root }), "must not contain the output directory"),
            ("language", Win(r => r with { Language = "xx-NOPE" }), "language must be"),
            ("disk size", Win(r => r with { DiskGb = 1 }), "diskGb must be"),
            ("linux parameter on windows", Win(r => r with { SshPublicKey = Key }), "applies to osFamily=linux only"),
            ("windows parameters on linux", Lin(r => r with { IsoPath = iso, AdminUser = "x" }), "isoPath, adminUser apply to osFamily=windows only"),
            ("no key or password", Lin(r => r with { SshPublicKey = null }), "sshPublicKey is required unless password is given"),
            ("bad key", Lin(r => r with { SshPublicKey = "ssh-dss AAAAB3NzaC1kc3M=" }), "OpenSSH public key"),
            ("root user", Lin(r => r with { SshUser = "root" }), "sshUser must be"),
            ("hostname", Lin(r => r with { Hostname = "-bad-" }), "hostname must be"),
            ("package", Lin(r => r with { Packages = ["Bad Package"] }), "Invalid package name"),
            ("url and path", Lin(r => r with { ImageUrl = "https://example.invalid/x.img" }), "not both"),
            ("ftp url", Lin(r => r with { ImagePath = null, ImageUrl = "ftp://example.invalid/x.img" }), "https:// URL"),
            ("http without hash", Lin(r => r with { ImagePath = null, ImageUrl = "http://example.invalid/x.vhdx" }), "needs sha256"),
            ("bad hash", Lin(r => r with { Sha256 = "abc" }), "sha256 must be")
        ];
        mock.ClearCalls();
        var failures = new List<string>();
        foreach (var (name, request, expected) in invalid)
        {
            var error = await Expect<ArgumentException>(() => build.PrepareAsync(request, default));
            if (error is null || !error.Message.Contains(expected, StringComparison.Ordinal)) failures.Add($"{name}: {error?.Message ?? "accepted"}");
        }
        check(failures.Count == 0 && mock.Calls().Count == 0 && !Directory.Exists(output),
            $"{invalid.Length} invalid requests are rejected before any host call or file creation" + (failures.Count == 0 ? "" : ": " + string.Join(" / ", failures)));

        var sanitized = await build.PrepareAsync(Win(r => r with { Name = "My_Lab.Image-01" }), default);
        check(sanitized.Windows!.ComputerName == "MY-LAB-IMAGE-01" && sanitized.Windows.AdminUser == "labadmin" && sanitized.Windows.PasswordGenerated
            && sanitized.Windows.AdminPassword.Length == 20 && sanitized.DiskGb == 100 && sanitized.Windows.Language == "en-US"
            && sanitized.VhdPath == Path.Combine(output, "My_Lab.Image-01.vhdx") && sanitized.SidecarPath == Path.Combine(output, "My_Lab.Image-01.image.json"),
            "Defaults: computer name derived from name, labadmin, generated 20-character password, 100 GB, en-US, <name>.vhdx and <name>.image.json");

        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "lab.vhdx"), [1]);
        var existingVhd = await Expect<ArgumentException>(() => build.PrepareAsync(Win(), default));
        File.Delete(Path.Combine(output, "lab.vhdx"));
        File.WriteAllText(Path.Combine(output, "lab.image.json"), "{}");
        var existingSidecar = await Expect<ArgumentException>(() => build.PrepareAsync(Win(), default));
        File.Delete(Path.Combine(output, "lab.image.json"));
        File.WriteAllBytes(Path.Combine(output, "lab-seed.iso"), [1]);
        var existingSeed = await Expect<ArgumentException>(() => build.PrepareAsync(Lin(), default));
        File.Delete(Path.Combine(output, "lab-seed.iso"));
        check(new[] { existingVhd, existingSidecar, existingSeed }.All(e => e?.Message.Contains("already exists", StringComparison.Ordinal) == true),
            "Existing outputs (VHDX, sidecar, seed ISO) are refused");

        var savedPath = Environment.GetEnvironmentVariable("PATH");
        var savedQemu = Environment.GetEnvironmentVariable(ImageBuildService.QemuImgVariable);
        HyperVOperationException? missing, wrong;
        try
        {
            Environment.SetEnvironmentVariable("PATH", root);
            Environment.SetEnvironmentVariable(ImageBuildService.QemuImgVariable, null);
            missing = await Expect<HyperVOperationException>(() => build.PrepareAsync(Lin(r => r with { ImagePath = image }), default));
            Environment.SetEnvironmentVariable(ImageBuildService.QemuImgVariable, Path.Combine(root, "nowhere", "qemu-img.exe"));
            wrong = await Expect<HyperVOperationException>(() => build.PrepareAsync(Lin(r => r with { ImagePath = image }), default));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
            Environment.SetEnvironmentVariable(ImageBuildService.QemuImgVariable, savedQemu);
        }
        var qemuDefault = Environment.GetEnvironmentVariable(ImageBuildService.QemuImgVariable, EnvironmentVariableTarget.User) is null
            && Environment.GetEnvironmentVariable(ImageBuildService.QemuImgVariable, EnvironmentVariableTarget.Machine) is null
            && !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "qemu", "qemu-img.exe"));
        check((!qemuDefault || missing?.ErrorType == "NotFound" && missing.Message.Contains("HYPERV_CONTROL_QEMU_IMG", StringComparison.Ordinal)
                && missing.Message.Contains("qemu-img", StringComparison.Ordinal) && missing.Message.Contains("--elevate", StringComparison.Ordinal))
            && wrong?.ErrorType == "NotFound" && wrong.Message.Contains("HYPERV_CONTROL_QEMU_IMG (process scope)", StringComparison.Ordinal) && mock.Calls().Count == 0,
            "A missing qemu-img fails before any download with an error naming HYPERV_CONTROL_QEMU_IMG; a wrong path is named as such");

        var wait = await Expect<ArgumentException>(() => build.StartAsync(Win(), 5000, default));
        check(wait?.Message.Contains("waitSeconds", StringComparison.Ordinal) == true, "waitSeconds is bounded to 0..3600");
    }

    private static object ImageState(object? native = null, string? bcdbootHelp = null, string? failAt = null, string isoDriveLetter = "M") => new
    {
        vms = Array.Empty<object>(), switches = Array.Empty<object>(),
        imageBuild = new
        {
            isoDriveLetter, diskNumber = 7, driveLetters = new[] { "S", "W" }, createFiles = true, native, bcdbootHelp, failAt,
            images = new object[]
            {
                new { index = 1, name = "Windows 11 Enterprise N Evaluation", description = "N edition", size = 1L << 30, editionId = "EnterpriseNEval", architecture = 9, version = "10.0.26300.9457", installationType = "Client", languages = new[] { "en-US" } },
                new { index = 2, name = "Windows 11 Enterprise Evaluation", description = "Enterprise", size = 1L << 30, editionId = "EnterpriseEval", architecture = 9, version = "10.0.26300.9457", installationType = "Client", languages = new[] { "en-US" } }
            }
        }
    };

    private sealed class Recorder : IProgress<JobProgress>
    {
        public readonly List<string> Messages = [];
        public void Report(JobProgress value) { lock (Messages) if (value.Message is { } message) Messages.Add(message); }
    }

    private static string Tool(JsonElement p) => Path.GetFileName(p.GetProperty("FilePath").GetString() ?? "").ToLowerInvariant();
    // The base mock's logger unrolls one-element arrays to a scalar, so accept both shapes.
    private static string[] Args(JsonElement p) => !p.TryGetProperty("ArgumentList", out var a) ? []
        : a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
        : a.ValueKind == JsonValueKind.String ? [a.GetString()!] : [];
    private static string First(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value[0].GetString() ?? "" : value.GetString() ?? "";

    private static async Task WindowsPipeline(Action<bool, string> check, Func<string, string> temp)
    {
        var root = temp("windows");
        var isoContent = Directory.CreateDirectory(Path.Combine(root, "iso", "sources")).Parent!.FullName;
        File.WriteAllBytes(Path.Combine(isoContent, "sources", "install.wim"), [1, 2, 3]);
        var iso = Path.Combine(root, "media", "win11.iso");
        Directory.CreateDirectory(Path.GetDirectoryName(iso)!);
        File.WriteAllBytes(iso, Encoding.ASCII.GetBytes("not really an ISO"));
        var esp = Directory.CreateDirectory(Path.Combine(root, "esp")).FullName;
        var windows = Directory.CreateDirectory(Path.Combine(root, "win", "Windows", "System32")).Parent!.Parent!.FullName;
        var tools = Directory.CreateDirectory(Path.Combine(root, "tools", "sub")).Parent!.FullName;
        File.WriteAllText(Path.Combine(tools, "dbg.txt"), "tool");
        File.WriteAllBytes(Path.Combine(tools, "sub", "\u7a7a \u95f4.bin"), [9, 8, 7]);
        File.WriteAllText(Path.Combine(tools, ".hidden"), "h");
        File.SetAttributes(Path.Combine(tools, ".hidden"), FileAttributes.Hidden);
        var output = Path.Combine(root, "out");
        var isoHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(iso)));

        // 1. ISO source, default edition, KDNET, tools; bcdboot succeeds.
        using (var mock = new MockHyperV(ImageState()))
        using (var service = new ControlService())
        {
            var build = service.ImageBuild;
            build.DriveRoot = letter => letter switch { 'M' => isoContent, 'S' => esp, 'W' => windows, _ => throw new InvalidOperationException("Unexpected drive " + letter) };
            // The store bcdboot wrote, as the kernel-debug bcdedit session leaves it: TreatAsSystem unset, a log beside it.
            var espStore = Path.Combine(Directory.CreateDirectory(Path.Combine(esp, "EFI", "Microsoft", "Boot")).FullName, "BCD");
            ImageBuildService.CreateEmptyHive(espStore);
            File.WriteAllBytes(espStore + ".LOG1", new byte[512]);
            var plan = await build.PrepareAsync(new ImageBuildRequest
            {
                OsFamily = "windows", Name = "lab-win", OutputDirectory = output, IsoPath = iso, DiskGb = 64, ComputerName = "HVC-LAB", AdminPassword = Password,
                KernelDebug = new ImageBuildKernelDebug(true, "net", "192.0.2.10", 50005, "1a.2b.3c.4d"), ToolsDirectory = tools
            }, default);
            mock.ClearCalls();
            var progress = new Recorder();
            var result = (ImageBuildResult)(await build.ExecuteAsync(plan, progress, default))!;
            var calls = mock.Calls().ToList();
            int At(string cmdlet, Func<JsonElement, bool>? match = null) => calls.FindIndex(c => c.Cmdlet == cmdlet && (match?.Invoke(c.Parameters) ?? true));
            int[] order =
            [
                At("Mount-DiskImage", p => First(p.GetProperty("ImagePath")) == iso && p.GetProperty("Access").GetString() == "ReadOnly"),
                At("Get-WindowsImage", p => !p.TryGetProperty("Index", out _) && p.GetProperty("ImagePath").GetString() == @"M:\sources\install.wim"),
                At("Get-WindowsImage", p => p.TryGetProperty("Index", out var i) && i.GetInt32() == 2),
                At("New-VHD", p => p.GetProperty("Path").GetString() == plan.VhdPath && p.GetProperty("SizeBytes").GetInt64() == 64L << 30 && p.GetProperty("Dynamic").GetBoolean()),
                At("Mount-VHD", p => First(p.GetProperty("Path")) == plan.VhdPath),
                At("Initialize-Disk", p => p.GetProperty("Number").GetInt32() == 7 && p.GetProperty("PartitionStyle").GetString() == "GPT"),
                At("Remove-Partition", p => p.GetProperty("PartitionNumber").GetInt32() == 1),
                At("New-Partition", p => p.GetProperty("Size").GetInt64() == 300L << 20 && p.GetProperty("GptType").GetString() == ImageBuildScripts.BasicDataGptType),
                At("Format-Volume", p => p.GetProperty("PartitionNumber").GetInt32() == 1 && p.GetProperty("FileSystem").GetString() == "FAT32"),
                At("Set-Partition", p => p.GetProperty("PartitionNumber").GetInt32() == 1 && p.GetProperty("GptType").GetString() == ImageBuildScripts.EspGptType),
                At("Add-PartitionAccessPath", p => p.GetProperty("PartitionNumber").GetInt32() == 1 && p.GetProperty("AssignDriveLetter").GetBoolean()),
                At("New-Partition", p => p.GetProperty("Size").GetInt64() == 16L << 20 && p.GetProperty("GptType").GetString() == ImageBuildScripts.MsrGptType),
                At("New-Partition", p => p.TryGetProperty("UseMaximumSize", out var m) && m.GetBoolean() && p.GetProperty("GptType").GetString() == ImageBuildScripts.BasicDataGptType),
                At("Format-Volume", p => p.GetProperty("PartitionNumber").GetInt32() == 3 && p.GetProperty("FileSystem").GetString() == "NTFS"),
                At("Add-PartitionAccessPath", p => p.GetProperty("PartitionNumber").GetInt32() == 3),
                At("Expand-WindowsImage", p => p.GetProperty("ImagePath").GetString() == @"M:\sources\install.wim" && p.GetProperty("Index").GetInt32() == 2 && p.GetProperty("ApplyPath").GetString() == @"W:\"),
                At("Invoke-ImageBuildNative", p => Tool(p) == "bcdboot.exe" && Args(p).SequenceEqual(["/?"])),
                At("Invoke-ImageBuildNative", p => Tool(p) == "bcdboot.exe" && Args(p).SequenceEqual([@"W:\Windows", "/s", "S:", "/f", "UEFI", "/v", "/offline"])),
                At("Invoke-ImageBuildNative", p => Tool(p) == "bcdedit.exe" && Args(p).SequenceEqual(["/store", Store, "/set", "{default}", "debug", "on"])),
                At("Invoke-ImageBuildNative", p => Tool(p) == "bcdedit.exe" && Args(p).SequenceEqual(["/store", Store, "/dbgsettings", "net", "hostip:192.0.2.10", "port:50005", "key:1a.2b.3c.4d"])),
                At("Dismount-VHD", p => First(p.GetProperty("Path")) == plan.VhdPath),
                At("Dismount-DiskImage", p => First(p.GetProperty("ImagePath")) == iso)
            ];
            var bcdbootRuns = calls.Where(c => c.Cmdlet == "Invoke-ImageBuildNative" && Tool(c.Parameters) == "bcdboot.exe" && !Args(c.Parameters).Contains("/?")).ToList();
            var bcdeditRuns = calls.Where(c => c.Cmdlet == "Invoke-ImageBuildNative" && Tool(c.Parameters) == "bcdedit.exe").ToList();
            check(order.All(i => i >= 0) && order.Zip(order.Skip(1)).All(p => p.First < p.Second)
                && bcdbootRuns.Count == 1 && bcdbootRuns.All(c => Args(c.Parameters).Contains("/s")) && bcdeditRuns.All(c => Args(c.Parameters).Take(2).SequenceEqual(["/store", Store])),
                "Windows build runs mount, image selection, GPT layout, apply, bcdboot /s and offline bcdedit in order, then dismounts VHDX and ISO; host boot settings are never targeted"
                + (order.All(i => i >= 0) ? "" : " (missing step " + Array.IndexOf(order, -1) + ")"));

            var unattendPath = Path.Combine(windows, "Windows", "Panther", "unattend.xml");
            var unattendBytes = File.ReadAllBytes(unattendPath);
            XNamespace u = ImageBuildUnattend.Namespace;
            var unattend = XDocument.Parse(Encoding.UTF8.GetString(unattendBytes, 3, unattendBytes.Length - 3));
            check(unattendBytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }) && unattend.Descendants(u + "ComputerName").Single().Value == "HVC-LAB"
                && unattend.Descendants(u + "Password").All(p => p.Element(u + "Value")!.Value == Password),
                "Panther\\unattend.xml is written into the applied image with the computer name and the escaped password");
            check(File.ReadAllText(Path.Combine(windows, "Tools", "dbg.txt")) == "tool" && File.ReadAllBytes(Path.Combine(windows, "Tools", "sub", "\u7a7a \u95f4.bin")).SequenceEqual(new byte[] { 9, 8, 7 })
                && File.Exists(Path.Combine(windows, "Tools", ".hidden")) && result.ToolsCopied is ImageBuildToolsCopy { Files: 3, GuestPath: "C:\\Tools" },
                "toolsDirectory is copied to C:\\Tools including nested Unicode and hidden files");

            var sidecarText = File.ReadAllText(plan.SidecarPath);
            using var sidecar = JsonDocument.Parse(sidecarText);
            var s = sidecar.RootElement;
            check(s.GetProperty("name").GetString() == "lab-win" && s.GetProperty("osFamily").GetString() == "windows" && s.GetProperty("kind").GetString() == "template"
                && s.GetProperty("config").GetProperty("secureBoot").GetBoolean() == false && s.GetProperty("config").GetProperty("secureBootTemplate").GetString() == "MicrosoftWindows"
                && s.GetProperty("config").GetProperty("enhancedSessionTransportType").GetString() == "VMBus" && s.GetProperty("config").GetProperty("tpm").GetBoolean()
                && s.GetProperty("config").GetProperty("staticMemory").GetBoolean() && s.GetProperty("config").GetProperty("checkpointType").GetString() == "Standard"
                && s.GetProperty("adminUser").GetString() == "labadmin" && s.GetProperty("sshUser").ValueKind == JsonValueKind.Null
                && s.GetProperty("builtFrom").GetProperty("kind").GetString() == "iso" && s.GetProperty("builtFrom").GetProperty("sha256").GetString() == isoHash
                && s.GetProperty("image").GetProperty("index").GetInt32() == 2 && s.GetProperty("kernelDebug").GetProperty("transport").GetString() == "net"
                && !s.GetProperty("kernelDebug").GetProperty("keyStored").GetBoolean() && s.TryGetProperty("builtAt", out _)
                && s.GetProperty("warnings").EnumerateArray().Any(w => w.GetString()!.Contains("Secure Boot off", StringComparison.Ordinal))
                && !sidecarText.Contains("w0rd", StringComparison.Ordinal) && !sidecarText.Contains("1a.2b.3c.4d", StringComparison.Ordinal),
                "Sidecar <name>.image.json records template config (Secure Boot off for kernel debugging), admin user, source hash, image and warnings, never the password or KDNET key");
            var mockState = mock.State().GetProperty("imageBuild");
            check(result.BootConfiguration == "bcdboot /offline" && result.KernelDebugApplied == true && result.Partitions!.Select(p => p.GptType)
                    .SequenceEqual([ImageBuildScripts.EspGptType, ImageBuildScripts.MsrGptType, ImageBuildScripts.BasicDataGptType])
                && result.Warnings.Any(w => w.Contains("90 days", StringComparison.Ordinal)) && result.NextSteps.Any(n => n.Contains("hyperv_credential", StringComparison.Ordinal))
                && result.NextSteps.Any(n => n.Contains("Secure Boot Off", StringComparison.Ordinal)) && result.VhdVirtualBytes == 64L << 30
                && progress.Messages.Any(m => m.StartsWith("windows 5/10: applying image 2", StringComparison.Ordinal))
                && mockState.GetProperty("attachedVhds").GetArrayLength() == 0 && mockState.GetProperty("mountedImages").GetArrayLength() == 0,
                "Result reports partitions, boot method, applied debug settings, evaluation warning, nextSteps and phase progress; nothing stays mounted");
            check(ImageBuildService.ReadSystemStoreFlags(espStore) == ("BCD00000000", 1, 1) && !File.Exists(espStore + ".LOG1"),
                "After the offline kernel-debug bcdedit session the store is marked as a system store again (System=1, TreatAsSystem=1) and its stale log is deleted");
        }

        // 2. install.wim source with an edition match; bcdboot copies files but cannot create the store (c0000035): bcdedit fallback.
        var esp2 = Path.Combine(root, "esp2");
        Directory.CreateDirectory(Path.Combine(esp2, "EFI", "Microsoft", "Boot"));
        Directory.CreateDirectory(Path.Combine(esp2, "EFI", "Boot"));
        File.WriteAllBytes(Path.Combine(esp2, "EFI", "Boot", "bootx64.efi"), [1]);
        File.WriteAllBytes(Path.Combine(esp2, "EFI", "Microsoft", "Boot", "bootmgfw.efi"), [1]);
        File.WriteAllBytes(Path.Combine(esp2, "EFI", "Microsoft", "Boot", "BCD"), new byte[8192]);
        File.WriteAllBytes(Path.Combine(esp2, "EFI", "Microsoft", "Boot", "BCD.LOG"), new byte[8192]);
        var windows2 = Directory.CreateDirectory(Path.Combine(root, "win2", "Windows", "System32", "config")).Parent!.Parent!.Parent!.FullName;
        // A real (empty) hive: like BCD-Template, it has no Description System or TreatAsSystem value.
        var template = Path.Combine(windows2, "Windows", "System32", "config", "BCD-Template");
        ImageBuildService.CreateEmptyHive(template);
        var wim = Path.Combine(root, "install.wim");
        File.WriteAllBytes(wim, [4, 5, 6]);
        using (var mock = new MockHyperV(ImageState(native: new Dictionary<string, object>
            { ["bcdboot.exe"] = new { exitCode = 183, stdout = "BFSVC: Copy files which lack a version\nBFSVC Error: Failed to create a new system store. Status = [c0000035]", stderr = "" } })))
        using (var service = new ControlService())
        {
            var build = service.ImageBuild;
            build.DriveRoot = letter => letter switch { 'S' => esp2, 'W' => windows2, _ => throw new InvalidOperationException("Unexpected drive " + letter) };
            var plan = await build.PrepareAsync(new ImageBuildRequest
            {
                OsFamily = "windows", Name = "lab-wim", OutputDirectory = output, ImagePath = wim, Edition = "Enterprise N", DiskGb = 32,
                EnableRdp = false, AutoLogon = false, PreventDeviceEncryption = false
            }, default);
            mock.ClearCalls();
            var result = (ImageBuildResult)(await build.ExecuteAsync(plan, new Recorder(), default))!;
            var calls = mock.Calls();
            var stores = calls.Where(c => c.Cmdlet == "Invoke-ImageBuildNative" && Tool(c.Parameters) == "bcdedit.exe").Select(c => Args(c.Parameters)).ToList();
            var bootDirectory = Path.Combine(esp2, "EFI", "Microsoft", "Boot");
            XNamespace u = ImageBuildUnattend.Namespace;
            var unattend = XDocument.Load(Path.Combine(windows2, "Windows", "Panther", "unattend.xml"));
            check(!calls.Any(c => c.Cmdlet is "Mount-DiskImage" or "Dismount-DiskImage")
                && calls.Any(c => c.Cmdlet == "Expand-WindowsImage" && c.Parameters.GetProperty("ImagePath").GetString() == wim && c.Parameters.GetProperty("Index").GetInt32() == 1)
                && ImageBuildService.ReadSystemStoreFlags(Path.Combine(bootDirectory, "BCD")) == ("BCD00000000", 1, 1) && ImageBuildService.ReadSystemStoreFlags(template) == ("", -1, -1)
                && Directory.GetFiles(bootDirectory, "BCD*").Select(f => Path.GetFileName(f)).SequenceEqual(["BCD"])
                && stores.Count == 31 && stores.All(a => a.Take(2).SequenceEqual(["/store", Store]))
                && stores.Any(a => a.Length == 8 && a[2] == "/create" && a[4..].SequenceEqual(["/d", "Windows", "/application", "osloader"]))
                && result.BootConfiguration == "bcdboot /offline (files) + bcdedit (store)" && result.Warnings.Any(w => w.Contains("c0000035", StringComparison.Ordinal))
                && !unattend.Descendants(u + "AutoLogon").Any() && !unattend.Descendants(u + "fDenyTSConnections").Any(),
                "install.wim source with edition match; when bcdboot copies the boot files but cannot create the store (c0000035) the store is built from the image's BCD-Template "
                + "with bcdedit, then marked as a system store (KeyName BCD00000000, System=1, TreatAsSystem=1) with no log left beside it; the template itself is unchanged");
        }

        // 3. bcdboot fails outright: the job fails with bcdboot's reason, dismounts and deletes the VHDX.
        using (var mock = new MockHyperV(ImageState(native: new Dictionary<string, object>
            { ["bcdboot.exe"] = new { exitCode = 193, stdout = "BFSVC Error: Failed to validate boot manager checksum\nFailure when attempting to copy boot files.", stderr = "" } })))
        using (var service = new ControlService())
        {
            var build = service.ImageBuild;
            var esp3 = Directory.CreateDirectory(Path.Combine(root, "esp3")).FullName;
            build.DriveRoot = letter => letter switch { 'M' => isoContent, 'S' => esp3, 'W' => windows, _ => throw new InvalidOperationException("Unexpected drive " + letter) };
            var plan = await build.PrepareAsync(new ImageBuildRequest { OsFamily = "windows", Name = "lab-fail", OutputDirectory = output, IsoPath = iso, DiskGb = 32 }, default);
            mock.ClearCalls();
            var error = await Expect<HyperVOperationException>(() => build.ExecuteAsync(plan, new Recorder(), default));
            var calls = mock.Calls();
            check(error?.Message.StartsWith("bcdboot failed (exit 193): BFSVC Error: Failed to validate boot manager checksum | Failure when attempting to copy boot files.", StringComparison.Ordinal) == true
                && calls.Any(c => c.Cmdlet == "Dismount-VHD") && calls.Any(c => c.Cmdlet == "Dismount-DiskImage")
                && !File.Exists(plan.VhdPath) && !File.Exists(plan.SidecarPath) && mock.State().GetProperty("imageBuild").GetProperty("attachedVhds").GetArrayLength() == 0,
                "A bcdboot failure fails the build with bcdboot's own reason; the VHDX and ISO are dismounted and the partial VHDX is deleted");
        }

        // 4. A failure inside DISM apply on a WIM source: dismount, delete, no ISO steps.
        using (var mock = new MockHyperV(ImageState(failAt: "Expand-WindowsImage")))
        using (var service = new ControlService())
        {
            var build = service.ImageBuild;
            build.DriveRoot = letter => letter switch { 'S' => esp, 'W' => windows, _ => throw new InvalidOperationException("Unexpected drive " + letter) };
            var plan = await build.PrepareAsync(new ImageBuildRequest { OsFamily = "windows", Name = "lab-dism", OutputDirectory = output, ImagePath = wim, DiskGb = 32 }, default);
            mock.ClearCalls();
            var error = await Expect<HyperVOperationException>(() => build.ExecuteAsync(plan, new Recorder(), default));
            var calls = mock.Calls();
            check(error?.Message.Contains("Mock failure injected at Expand-WindowsImage", StringComparison.Ordinal) == true && calls.Any(c => c.Cmdlet == "Dismount-VHD")
                && !calls.Any(c => c.Cmdlet == "Dismount-DiskImage") && !calls.Any(c => c.Cmdlet == "Invoke-ImageBuildNative") && !File.Exists(plan.VhdPath),
                "A DISM failure stops before bcdboot, dismounts the VHDX and deletes it");
        }

        // 5. source=eval-iot through a loopback server: cached download, install.esd on the mounted ISO.
        using var server = new LoopbackHttpServer();
        var media = Encoding.ASCII.GetBytes(new string('x', 200_000));
        server.Add("/media/iot.iso", media, "\"iot\"");
        server.Redirect("/fwlink?linkid=iot", "/media/iot.iso");
        var esdContent = Directory.CreateDirectory(Path.Combine(root, "esd", "sources")).Parent!.FullName;
        File.WriteAllBytes(Path.Combine(esdContent, "sources", "install.esd"), [1]);
        using (var mock = new MockHyperV(ImageState()))
        using (var service = new ControlService())
        {
            var build = service.ImageBuild;
            build.EvaluationUrls = new Dictionary<string, Uri> { ["eval-iot"] = new(server.Base, "fwlink?linkid=iot") };
            build.DriveRoot = letter => letter switch { 'M' => esdContent, 'S' => esp, 'W' => windows, _ => throw new InvalidOperationException("Unexpected drive " + letter) };
            File.Delete(Path.Combine(windows, "Windows", "Panther", "unattend.xml"));
            Directory.Delete(Path.Combine(windows, "Tools"), true);
            var plan = await build.PrepareAsync(new ImageBuildRequest { OsFamily = "windows", Name = "lab-eval", OutputDirectory = output, Source = "eval-iot", DiskGb = 32 }, default);
            mock.ClearCalls();
            var result = (ImageBuildResult)(await build.ExecuteAsync(plan, new Recorder(), default))!;
            var cached = Path.Combine(output, "cache", "iot.iso");
            var calls = mock.Calls();
            var from = result.Sidecar.BuiltFrom;
            check(File.ReadAllBytes(cached).SequenceEqual(media) && calls.Any(c => c.Cmdlet == "Mount-DiskImage" && First(c.Parameters.GetProperty("ImagePath")) == cached)
                && calls.Any(c => c.Cmdlet == "Get-WindowsImage" && c.Parameters.GetProperty("ImagePath").GetString() == @"M:\sources\install.esd")
                && from.Kind == "eval-download" && from.EvalSource == "eval-iot" && from.Sha256 == Convert.ToHexStringLower(SHA256.HashData(media)) && !from.ChecksumVerified
                && from.FinalUrl!.EndsWith("/media/iot.iso", StringComparison.Ordinal) && result.Warnings.Any(w => w.Contains("no checksum", StringComparison.Ordinal))
                && result.Warnings.Count(w => w.Contains("90 days", StringComparison.Ordinal)) == 1,
                "source=eval-iot downloads the evaluation ISO into <output>\\cache (kept), mounts it and applies install.esd; the unverified hash is recorded with a warning");
        }

        var existingTools = Directory.CreateDirectory(Path.Combine(root, "image-with-tools", "Tools")).FullName;
        File.WriteAllText(Path.Combine(existingTools, "vendor.txt"), "image file");
        var merged = ImageBuildService.CopyTree(tools, existingTools, "C:\\Tools");
        File.WriteAllText(Path.Combine(existingTools, "conflict.txt"), "image file");
        var conflictSource = Directory.CreateDirectory(Path.Combine(root, "conflict-tools")).FullName;
        File.WriteAllText(Path.Combine(conflictSource, "conflict.txt"), "tool file");
        var conflict = Assert(() => ImageBuildService.CopyTree(conflictSource, existingTools, "C:\\Tools"));
        check(merged.Files == 3 && File.ReadAllText(Path.Combine(existingTools, "vendor.txt")) == "image file" && File.Exists(Path.Combine(existingTools, "dbg.txt"))
            && conflict?.Message.Contains("C:\\Tools\\conflict.txt already exists in the image", StringComparison.Ordinal) == true
            && File.ReadAllText(Path.Combine(existingTools, "conflict.txt")) == "image file",
            "An existing C:\\Tools in the image is merged, but an image file is never overwritten");

        var listJson = JsonDocument.Parse("""[{"index":1,"name":"Windows 11 Pro N"},{"index":2,"name":"Windows 11 Pro"},{"index":3,"name":"Windows 11 Pro for Workstations"}]""").RootElement;
        var ambiguous = Assert(() => ImageBuildService.SelectImage(listJson, null, "Pro"));
        var unknown = Assert(() => ImageBuildService.SelectImage(listJson, null, "Home"));
        var badIndex = Assert(() => ImageBuildService.SelectImage(listJson, 9, null));
        check(ImageBuildService.SelectImage(listJson, null, null) == 2 && ImageBuildService.SelectImage(listJson, null, "windows 11 pro") == 2
            && ImageBuildService.SelectImage(listJson, null, "Workstations") == 3 && ImageBuildService.SelectImage(listJson, 1, null) == 1
            && ambiguous?.ErrorType == "AmbiguousName" && unknown?.ErrorType == "NotFound" && badIndex?.ErrorType == "NotFound" && badIndex.Message.Contains("2: Windows 11 Pro;", StringComparison.Ordinal),
            "Image selection: default skips N editions; edition matches exactly first, then one unique substring; ambiguity and misses list the images");
    }

    private static async Task LinuxPipeline(Action<bool, string> check, Func<string, string> temp)
    {
        if (Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage") is null)
        {
            Console.WriteLine("SKIP Linux pipeline: the IMAPI2 file-system COM class is not registered on this machine.");
            return;
        }
        var root = temp("linux");
        var output = Path.Combine(root, "out");
        var qemu = Path.Combine(root, "qemu", "qemu-img.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(qemu)!);
        File.WriteAllBytes(qemu, [0]);
        using var server = new LoopbackHttpServer();
        var cloudImage = Encoding.ASCII.GetBytes("QFI\u00fb fake qcow2 cloud image");
        var cloudHash = Convert.ToHexStringLower(SHA256.HashData(cloudImage));
        server.Add("/releases/24.04/release/ubuntu-24.04-server-cloudimg-amd64.img", cloudImage, "\"img\"");
        server.Add("/releases/24.04/release/SHA256SUMS", Encoding.ASCII.GetBytes($"{new string('b', 64)} *ubuntu-24.04-server-cloudimg-amd64.vmdk\n{cloudHash} *ubuntu-24.04-server-cloudimg-amd64.img\n"));
        var savedQemu = Environment.GetEnvironmentVariable(ImageBuildService.QemuImgVariable);
        Environment.SetEnvironmentVariable(ImageBuildService.QemuImgVariable, qemu);
        try
        {
            using var mock = new MockHyperV(new { vms = Array.Empty<object>(), switches = Array.Empty<object>() });
            using var service = new ControlService();
            var build = service.ImageBuild;
            build.UbuntuImageUrl = new Uri(server.Base, "releases/24.04/release/ubuntu-24.04-server-cloudimg-amd64.img");
            var plan = await build.PrepareAsync(new ImageBuildRequest { OsFamily = "linux", Name = "lab-ubuntu", OutputDirectory = output, SshPublicKey = Key, DiskGb = 100 }, default);
            mock.ClearCalls();
            var result = (ImageBuildResult)(await build.ExecuteAsync(plan, new Recorder(), default))!;
            var cached = Path.Combine(output, "cache", "ubuntu-24.04-server-cloudimg-amd64.img");
            var calls = mock.Calls().ToList();
            int At(Func<(string Cmdlet, JsonElement Parameters), bool> match) => calls.FindIndex(c => match(c));
            int[] order =
            [
                At(c => c.Cmdlet == "Invoke-ImageBuildNative" && c.Parameters.GetProperty("FilePath").GetString() == qemu && Args(c.Parameters).SequenceEqual(["info", "--output=json", cached])),
                At(c => c.Cmdlet == "Invoke-ImageBuildNative" && Args(c.Parameters).SequenceEqual(["convert", "-f", "qcow2", "-O", "vhdx", "-o", "subformat=dynamic", cached, plan.VhdPath])),
                At(c => c.Cmdlet == "Get-VHD"),
                At(c => c.Cmdlet == "Resize-VHD" && c.Parameters.GetProperty("Path").GetString() == plan.VhdPath && c.Parameters.GetProperty("SizeBytes").GetInt64() == 100L << 30)
            ];
            check(order.All(i => i >= 0) && order.Zip(order.Skip(1)).All(p => p.First < p.Second) && File.ReadAllBytes(cached).SequenceEqual(cloudImage),
                "Linux build downloads and verifies the cloud image against SHA256SUMS, then runs qemu-img info, convert (QCOW2 to dynamic VHDX) and Resize-VHD in order");

            var iso = File.ReadAllBytes(plan.SeedIsoPath!);
            var (primary, label, joliet) = ReadIsoDescriptors(iso);
            var text = Encoding.Latin1.GetString(iso);
            var userDataAt = text.IndexOf("#cloud-config\n", StringComparison.Ordinal);
            check(primary && label == "CIDATA" && joliet && ContainsUtf16BigEndian(iso, "user-data") && ContainsUtf16BigEndian(iso, "meta-data")
                && userDataAt > 0 && !(iso[userDataAt - 3] == 0xEF && iso[userDataAt - 2] == 0xBB && iso[userDataAt - 1] == 0xBF)
                && text.Contains("hostname: \"lab-ubuntu\"\nusers:\n  - name: \"lab\"", StringComparison.Ordinal) && text.Contains("  - \"linux-cloud-tools-virtual\"\n", StringComparison.Ordinal)
                && text.Contains("instance-id: \"iid-", StringComparison.Ordinal) && !text[userDataAt..(userDataAt + 400)].Contains('\r'),
                "The seed ISO (built for real with IMAPI2) has an ISO 9660 primary volume descriptor at sector 16 labelled CIDATA, Joliet names user-data and meta-data, and LF-only content without a BOM");

            using var sidecar = JsonDocument.Parse(File.ReadAllText(plan.SidecarPath));
            var s = sidecar.RootElement;
            check(s.GetProperty("osFamily").GetString() == "linux" && s.GetProperty("sshUser").GetString() == "lab" && s.GetProperty("adminUser").ValueKind == JsonValueKind.Null
                && s.GetProperty("seedIsoPath").GetString() == plan.SeedIsoPath && s.GetProperty("config").GetProperty("secureBoot").GetBoolean()
                && s.GetProperty("config").GetProperty("secureBootTemplate").GetString() == "MicrosoftUEFICertificateAuthority"
                && s.GetProperty("builtFrom").GetProperty("checksumVerified").GetBoolean() && s.GetProperty("builtFrom").GetProperty("sha256").GetString() == cloudHash
                && s.GetProperty("builtFrom").GetProperty("checksumSource").GetString()!.EndsWith("/SHA256SUMS", StringComparison.Ordinal)
                && result.SeedIsoBytes == iso.Length && result.NextSteps.Any(n => n.Contains("attach_iso", StringComparison.Ordinal)),
                "Linux sidecar records the UEFI CA Secure Boot template, the seed ISO, the SSH user and the verified checksum");
        }
        finally { Environment.SetEnvironmentVariable(ImageBuildService.QemuImgVariable, savedQemu); }

        // A .vhdx source needs no qemu-img: it is copied, never shrunk, and a password-only user gets SSH password login.
        var vhdx = Path.Combine(root, "prebuilt.vhdx");
        File.WriteAllBytes(vhdx, Encoding.ASCII.GetBytes("vhdxfile"));
        using (var mock = new MockHyperV(new { vms = Array.Empty<object>(), switches = Array.Empty<object>() }))
        using (var service = new ControlService())
        {
            var savedPath = Environment.GetEnvironmentVariable("PATH");
            ImageBuildPlan plan;
            try
            {
                Environment.SetEnvironmentVariable("PATH", root);
                plan = await service.ImageBuild.PrepareAsync(new ImageBuildRequest { OsFamily = "linux", Name = "lab-copy", OutputDirectory = output, ImagePath = vhdx, Password = "secret!", Hostname = "Copy-Host" }, default);
            }
            finally { Environment.SetEnvironmentVariable("PATH", savedPath); }
            mock.ClearCalls();
            var result = (ImageBuildResult)(await service.ImageBuild.ExecuteAsync(plan, new Recorder(), default))!;
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(plan.SeedIsoPath!));
            check(plan.Linux!.QemuImg is null && File.ReadAllBytes(plan.VhdPath).SequenceEqual(File.ReadAllBytes(vhdx)) && !mock.Called("Resize-VHD") && !mock.Called("Invoke-ImageBuildNative")
                && result.Warnings.Any(w => w.Contains("larger than diskGb=40", StringComparison.Ordinal)) && text.Contains("ssh_pwauth: true", StringComparison.Ordinal)
                && text.Contains("passwd: \"$6$", StringComparison.Ordinal) && !text.Contains("secret!", StringComparison.Ordinal) && text.Contains("hostname: \"copy-host\"", StringComparison.Ordinal),
                "A .vhdx source is copied without qemu-img and never shrunk; a password-only user gets a SHA-512-crypt hash and SSH password login");
        }
    }

    private static async Task ToolContract(Action<bool, string> check, Func<string, string> temp)
    {
        var root = temp("tool");
        var output = Path.Combine(root, "out");
        var wim = Path.Combine(root, "install.wim");
        File.WriteAllBytes(wim, [1]);
        var esp = Directory.CreateDirectory(Path.Combine(root, "esp")).FullName;
        var windows = Directory.CreateDirectory(Path.Combine(root, "win", "Windows")).Parent!.FullName;
        using var mock = new MockHyperV(ImageState());
        using var service = new ControlService();
        service.ImageBuild.DriveRoot = letter => letter switch { 'S' => esp, 'W' => windows, _ => throw new InvalidOperationException("Unexpected drive " + letter) };
        var tools = new HyperVTools(service);

        var schema = McpServerTool.Create(typeof(HyperVTools).GetMethod(nameof(HyperVTools.ImageBuild))!, tools).ProtocolTool.InputSchema;
        var properties = schema.GetProperty("properties");
        var kernel = properties.GetProperty("kernelDebug");
        var kernelProperties = (kernel.TryGetProperty("properties", out var direct) ? direct
            : kernel.GetProperty("anyOf").EnumerateArray().First(a => a.TryGetProperty("properties", out _)).GetProperty("properties"));
        check(schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToHashSet().SetEquals(["osFamily", "name"])
            && new[] { "enabled", "transport", "hostIp", "port", "key", "comPort" }.All(p => kernelProperties.TryGetProperty(p, out _))
            && properties.TryGetProperty("packages", out _) && properties.TryGetProperty("waitSeconds", out _) && properties.GetProperty("adminPassword").TryGetProperty("description", out _),
            "hyperv_image_build schema: osFamily and name required, kernelDebug is an object {enabled, transport, hostIp, port, key, comPort}, parameters are described");

        var refused = await tools.ImageBuild("linux", "lab", outputDirectory: output, sshPublicKey: Key, sshUser: "root");
        using (var document = JsonDocument.Parse(refused.Content.OfType<TextContentBlock>().Single().Text))
            check(refused.IsError == true && !document.RootElement.GetProperty("ok").GetBoolean() && document.RootElement.GetProperty("errorType").GetString() == "ArgumentException",
                "Tool validation failures return the uniform error envelope before a job starts");

        var started = await tools.ImageBuild("windows", "tool-win", outputDirectory: output, imagePath: wim, diskGb: 32, waitSeconds: 120);
        var text = started.Content.OfType<TextContentBlock>().Single().Text;
        using (var document = JsonDocument.Parse(text))
        {
            var r = document.RootElement;
            var password = r.GetProperty("credentials").GetProperty("password").GetString()!;
            var job = r.GetProperty("job").GetRawText();
            check(started.IsError != true && r.GetProperty("ok").GetBoolean() && r.GetProperty("job").GetProperty("status").GetString() == "succeeded"
                && r.GetProperty("credentials").GetProperty("passwordGenerated").GetBoolean() && password.Length == 20
                && r.GetProperty("credentials").GetProperty("warning").GetString()!.Contains("hyperv_credential", StringComparison.Ordinal)
                && !job.Contains(password, StringComparison.Ordinal) && !File.ReadAllText(Path.Combine(output, "tool-win.image.json")).Contains(password, StringComparison.Ordinal)
                && r.GetProperty("job").GetProperty("result").GetProperty("vhdPath").GetString() == Path.Combine(output, "tool-win.vhdx")
                && r.GetProperty("outputs").GetProperty("sidecarPath").GetString() == Path.Combine(output, "tool-win.image.json"),
                "hyperv_image_build returns the job; a generated password appears once in the start response, never in the job result or sidecar");
        }
        var again = await tools.ImageBuild("windows", "tool-win", outputDirectory: output, imagePath: wim, diskGb: 32);
        check(again.IsError == true && again.Content.OfType<TextContentBlock>().Single().Text.Contains("already exists", StringComparison.Ordinal),
            "A second build with the same name is refused because its outputs exist");
    }

    private static (bool Primary, string Label, bool Joliet) ReadIsoDescriptors(byte[] iso)
    {
        bool primary = false, joliet = false;
        var label = "";
        for (var sector = 16; (sector + 1) * 2048 <= iso.Length; sector++)
        {
            var o = sector * 2048;
            if (Encoding.ASCII.GetString(iso, o + 1, 5) != "CD001") break;
            if (iso[o] == 1) { primary = sector == 16; label = Encoding.ASCII.GetString(iso, o + 40, 32).TrimEnd(); }
            if (iso[o] == 2 && iso[o + 88] == 0x25 && iso[o + 89] == 0x2F && iso[o + 90] is 0x40 or 0x43 or 0x45) joliet = true;
            if (iso[o] == 255) break;
        }
        return (primary, label, joliet);
    }

    private static bool ContainsUtf16BigEndian(byte[] haystack, string value)
    {
        var needle = Encoding.BigEndianUnicode.GetBytes(value);
        return haystack.AsSpan().IndexOf(needle) >= 0;
    }

    private static async Task<T?> Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return null; }
        catch (T ex) { return ex; }
    }

    private static HyperVOperationException? Assert(Action action)
    {
        try { action(); return null; }
        catch (HyperVOperationException ex) { return ex; }
    }
}

/// <summary>
/// A minimal HTTP/1.1 file server on 127.0.0.1 for download tests: HEAD and GET, byte ranges, ETags, redirects, and
/// connections dropped mid-body once on request. No URL ACLs or administrator rights are needed.
/// </summary>
internal sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, (byte[] Body, string? ETag)> files = new();
    private readonly ConcurrentDictionary<string, string> redirects = new();
    private readonly ConcurrentDictionary<string, long> dropAfter = new();
    public ConcurrentQueue<(string Method, string Path, string? Range)> Requests { get; } = new();
    public Uri Base { get; }

    public LoopbackHttpServer()
    {
        listener.Start();
        Base = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        _ = AcceptAsync();
    }

    public void Add(string path, byte[] body, string? etag = null) => files[path] = (body, etag);
    public void Redirect(string path, string target) => redirects[path] = target;
    public void FailOnce(string path, long bytes) => dropAfter[path] = bytes;

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1, leaveOpen: true);
                while (true)
                {
                    var requestLine = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(requestLine)) return;
                    string? range = null;
                    for (var header = await reader.ReadLineAsync(); !string.IsNullOrEmpty(header); header = await reader.ReadLineAsync())
                        if (header.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = header[6..].Trim();
                    var parts = requestLine.Split(' ');
                    var (method, path) = (parts[0], parts[1]);
                    Requests.Enqueue((method, path, range));
                    if (redirects.TryGetValue(path, out var target))
                    {
                        await WriteAsync(stream, $"HTTP/1.1 302 Found\r\nLocation: {target}\r\nContent-Length: 0\r\n\r\n");
                        continue;
                    }
                    if (!files.TryGetValue(path, out var file))
                    {
                        await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n");
                        continue;
                    }
                    long start = 0;
                    if (method == "GET" && range is not null && range.StartsWith("bytes=", StringComparison.Ordinal) && range.EndsWith('-'))
                        start = long.Parse(range[6..^1], System.Globalization.CultureInfo.InvariantCulture);
                    if (start >= file.Body.Length && start > 0)
                    {
                        await WriteAsync(stream, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{file.Body.Length}\r\nContent-Length: 0\r\n\r\n");
                        continue;
                    }
                    var length = file.Body.Length - start;
                    var head = new StringBuilder(start > 0 ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n")
                        .Append("Content-Type: application/octet-stream\r\nAccept-Ranges: bytes\r\n").Append("Content-Length: ").Append(length).Append("\r\n");
                    if (start > 0) head.Append("Content-Range: bytes ").Append(start).Append('-').Append(file.Body.Length - 1).Append('/').Append(file.Body.Length).Append("\r\n");
                    if (file.ETag is not null) head.Append("ETag: ").Append(file.ETag).Append("\r\n");
                    await WriteAsync(stream, head.Append("\r\n").ToString());
                    if (method == "HEAD") continue;
                    if (dropAfter.TryRemove(path, out var cut) && cut < length)
                    {
                        await stream.WriteAsync(file.Body.AsMemory((int)start, (int)cut));
                        await stream.FlushAsync();
                        client.Client.Shutdown(SocketShutdown.Both);
                        return;
                    }
                    await stream.WriteAsync(file.Body.AsMemory((int)start, (int)length));
                    await stream.FlushAsync();
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static Task WriteAsync(NetworkStream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        stop.Dispose();
    }
}
