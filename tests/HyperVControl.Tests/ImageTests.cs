using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyperVControl.Services;
using ModelContextProtocol.Protocol;

namespace HyperVControl.Tests;

/// <summary>
/// Image catalog, library, fetch jobs and deploy against the recording mock module and a loopback HTTP
/// server: the saved live gallery feed, a UTF-8 custom catalog, Zip64 central directories, resume after an
/// interrupted download, cancellation cleanup, hash mismatches, the in-use guard, deploy rendering, the default
/// image and hyperv_vm_create's one-call quick create (fetch-then-deploy jobs, joined fetches, preflight refusals).
/// No test catalog that hyperv_vm_create can reach points at a real download URL.
/// </summary>
internal static class ImageTests
{
    private static readonly DateTimeOffset FeedDay = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly Version Host = new(10, 0, 26200, 0);
    private static readonly string[] LiveFeedNames =
        ["Windows 10 MSIX packaging environment", "Ubuntu 18.04 LTS", "Ubuntu 20.04 LTS", "Ubuntu 22.04 LTS", "Windows 11 dev environment"];

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "HyperVControl-images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var previousRoot = Environment.GetEnvironmentVariable(ImageCatalogService.RootVariable);
        var previousDefault = Environment.GetEnvironmentVariable(ImageCatalogService.DefaultImageVariable);
        Environment.SetEnvironmentVariable(ImageCatalogService.DefaultImageVariable, null);
        try
        {
            ParseFeeds(check);
            await ZipDirectories(check, temp);
            await using var server = new FixtureServer();
            await LibraryScenarios(check, temp, server);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ImageCatalogService.RootVariable, previousRoot);
            Environment.SetEnvironmentVariable(ImageCatalogService.DefaultImageVariable, previousDefault);
            foreach (var file in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void ParseFeeds(Action<bool, string> check)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "MockHyperV", "Fixtures", "GalleryHyperV.json"));
        check(bytes is [0xFF, 0xFE, ..] && Encoding.Unicode.GetString(bytes.AsSpan(2)).TrimEnd().EndsWith("}\r\n,\r\n]\r\n}"),
            "Fixture is the saved live feed: UTF-16 LE with a BOM and trailing commas");
        var feed = ImageCatalogFeed.Parse("microsoft", ImageCatalogFeed.Decode(bytes), Host, FeedDay);
        check(feed.Images.Select(i => i.Name).SequenceEqual(LiveFeedNames) && feed.Hidden == 0 && feed.Skipped.Count == 0,
            "The live feed parses leniently into its 5 images");
        var jammy = feed.Images.Single(i => i.Name == "Ubuntu 22.04 LTS");
        check(jammy.SecureBoot == false && jammy.EnhancedSessionTransportType == "HvSocket" && jammy.OsFamily == "linux"
            && jammy.Sha256 == "eb6b904aa40f7214bdeade25e7b189db8c25a021b711677c1049d61867ff20cf" && jammy.ArchiveRelativePath == "livecd.ubuntu-desktop-hyperv.vhdx"
            && jammy.DiskExtension == ".zip" && jammy.Fetchable && jammy.Stale && !jammy.EndOfLife && !jammy.Warnings.Any(w => w.StartsWith("Evaluation edition")),
            "Ubuntu 22.04: string secureBoot, HvSocket, SHA-256 from the prefixed hash, stale but still supported");
        var windev = feed.Images.Single(i => i.Name == "Windows 11 dev environment");
        var evaluation = windev.Warnings.Where(w => w.StartsWith("Evaluation edition:")).ToList();
        check(windev.Fetchable && !windev.Stale && !windev.EndOfLife && windev.OsFamily == "windows" && windev.SecureBoot == true && windev.Version == "10.0.22621"
            && windev.Sha256 == "b546c32894785dbaac07543d913bde9d6598972f76a440d6b5c00b2e73739684" && windev.ArchiveRelativePath == "WinDev2407Eval.vhdx"
            && evaluation.Count == 1 && evaluation[0].StartsWith("Evaluation edition: Windows 11 Enterprise Evaluation, last updated 2024-07-25.")
            && evaluation[0].Contains("Hyper-V Quick Create still downloads and runs it") && evaluation[0].Contains("run `slmgr /xpr` inside the guest for the end date")
            && evaluation[0].Contains("take a checkpoint after the first boot and recreate the VM from the image when it expires")
            && !windev.Warnings.Any(w => w.Contains("expired", StringComparison.OrdinalIgnoreCase) || w.Contains("black desktop") || w.Contains("shuts down")),
            "The Windows 11 dev environment is fetchable and current, with one evaluation-edition warning and no expiry verdict");
        var later = ImageCatalogFeed.Parse("microsoft", ImageCatalogFeed.Decode(bytes), Host, new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero))
            .Images.Single(i => i.Name == "Windows 11 dev environment");
        check(later.Fetchable && later.Stale && later.Warnings.Count(w => w.StartsWith("Evaluation edition:")) == 1,
            "No date turns the evaluation into an excluded image: parsed in 2030 it is only stale");
        var msix = feed.Images.Single(i => i.Name.StartsWith("Windows 10 MSIX"));
        check(msix.Fetchable && msix.Stale && msix.DiskExtension == ".zip"
            && msix.Warnings.Any(w => w.StartsWith("Evaluation edition: Windows 10 Enterprise Evaluation, last updated 2022-05-11.")),
            "The Windows 10 MSIX environment carries the same evaluation warning (and is stale), and its URI with spaces still parses");
        var bionic = feed.Images.Single(i => i.Name == "Ubuntu 18.04 LTS");
        check(bionic.EndOfLife && bionic.Stale && bionic.Warnings.Any(w => w.Contains("May 2023")), "Ubuntu 18.04 is flagged out of standard support");

        var custom = """
            // Comments, trailing commas and bool settings are accepted.
            { "Images": [
              { "name": "Bounded", "minHostVersion": "10.0.17763.0", "maxHostVersion": "10.0.99999.0", "lastUpdated": "2026-09-01T00:00:00Z",
                "config": { "secureBoot": true, "enhancedSessionTransportType": "vmbus", },
                "disk": { "uri": "C:\\gallery\\bounded.vhdx", "hash": "sha256:AA" }, },
              { "name": "Future host", "minHostVersion": "99.0.0.0", "disk": { "uri": "https://example.invalid/a.zip", "archiveRelativePath": "a.vhdx" } },
              { "name": "Old host", "maxHostVersion": "6.3.9600.0", "disk": { "uri": "https://example.invalid/b.zip", "archiveRelativePath": "b.vhdx" } },
              { "name": "Legacy VHD", "disk": { "uri": "https://example.invalid/legacy.vhd", "hash": "sha256:0000000000000000000000000000000000000000000000000000000000000000" } },
              { "publisher": "no name" },
            ], }
            """;
        var parsed = ImageCatalogFeed.Parse("custom", ImageCatalogFeed.Decode([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(custom)]), Host, FeedDay);
        var bounded = parsed.Images.Single(i => i.Name == "Bounded");
        var legacy = parsed.Images.Single(i => i.Name == "Legacy VHD");
        check(parsed.Images.Count == 2 && parsed.Hidden == 2 && parsed.Skipped.Count == 1
            && bounded.SecureBoot == true && bounded.EnhancedSessionTransportType == "VMBus" && !bounded.Fetchable && bounded.Warnings.Any(w => w.StartsWith("Invalid hash"))
            && !legacy.Fetchable && legacy.Warnings.Any(w => w.Contains("'.vhd'")),
            "A UTF-8 custom catalog: comments, trailing commas, bool settings, the host version filter, invalid hashes and .vhd disks");
    }

    private static async Task ZipDirectories(Action<bool, string> check, string temp)
    {
        var payload = Payload(1, 300_000);
        var zip = Path.Combine(temp, "two.zip");
        await File.WriteAllBytesAsync(zip, Zip(("notes/readme.txt", "hello"u8.ToArray()), ("Disk/Test.vhdx", payload)));
        var entries = await ImageZip.ReadCentralDirectoryAsync(zip, default);
        check(entries.Count == 2 && entries.Single(e => e.FullName == "Disk/Test.vhdx").UncompressedSize == payload.Length,
            "The tail-only central directory reader lists entries and uncompressed sizes");

        var zip64 = Path.Combine(temp, "zip64.zip");
        await File.WriteAllBytesAsync(zip64, Zip64("big/disk.vhdx", payload));
        var entries64 = await ImageZip.ReadCentralDirectoryAsync(zip64, default);
        check(entries64.Count == 1 && entries64[0].UncompressedSize == payload.Length && entries64[0].CompressedSize == payload.Length,
            "Zip64: sizes come from the extended-information field through the Zip64 end records");
        var extracted = Path.Combine(temp, "zip64.vhdx");
        await ImageFiles.ExtractAsync(zip64, @"BIG\Disk.VHDX", extracted, null, default);
        check(File.ReadAllBytes(extracted).AsSpan().SequenceEqual(payload) && ImageFiles.HasMagic(extracted, ".vhdx"),
            "Extraction matches the entry case-insensitively with either slash and streams a Zip64 archive");
        var iso = Path.Combine(temp, "magic.iso");
        var isoBytes = new byte[0x9000];
        "CD001"u8.CopyTo(isoBytes.AsSpan(0x8801));
        await File.WriteAllBytesAsync(iso, isoBytes);
        check(ImageFiles.HasMagic(iso, ".iso") && !ImageFiles.HasMagic(iso, ".vhdx") && ImageFiles.HasMagic(zip, ".zip"), "Magic bytes: CD001 at 0x8801, PK zip header");
        check(ImageFiles.FreeBytes(Path.GetPathRoot(temp)!) > 0 && ImageFiles.FreeBytes(temp) > 0, "Free space reads for a drive root and for a folder");
    }

    private static async Task LibraryScenarios(Action<bool, string> check, string temp, FixtureServer server)
    {
        var root = Path.Combine(temp, "library");
        var user = Directory.CreateDirectory(Path.Combine(temp, "user")).FullName;
        var vms = Path.Combine(temp, "vms");

        // Fixtures: distinct payloads so every archive has its own hash.
        var linux = Payload(2, 2_000_000);
        var testZip = Zip(("Disk/Test-Image.vhdx", linux));
        var interrupted = Payload(3, 3_000_000);
        var interruptZip = Zip(("disk.vhdx", interrupted));
        var stallZip = Zip(("disk.vhdx", Payload(4, 3_000_000)));
        var badZip = Zip(("disk.vhdx", Payload(5, 500_000)));
        var plain = Payload(6, 1_000_000);
        server.Add("/feeds/gallery.json", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "MockHyperV", "Fixtures", "GalleryHyperV.json")));
        server.Add("/images/test.zip", testZip);
        server.Add("/images/interrupt.zip", interruptZip);
        server.Add("/images/stall.zip", stallZip);
        server.Add("/images/bad.zip", badZip);
        server.Add("/images/plain.vhdx", plain);
        server.Redirect("/go/test", "/images/test.zip");
        server.DropOnce("/images/interrupt.zip", 1_000_000);
        server.Stall("/images/stall.zip", 600_000);
        var recent = DateTimeOffset.UtcNow.AddDays(-10).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var catalog = $$"""
            // HyperVControl test catalog
            { "images": [
              { "name": "Test Linux", "publisher": "Canonical Group Ltd", "version": "1.0", "lastUpdated": "{{recent}}",
                "description": ["Synthetic ", "test image", ],
                "config": { "secureBoot": false, "enhancedSessionTransportType": "HvSocket", },
                "disk": { "uri": "{{server.BaseUrl}}go/test", "hash": "sha256:{{Sha(testZip).ToUpperInvariant()}}", "archiveRelativePath": "disk/TEST-image.vhdx", }, },
              { "name": "Test Bad Hash", "publisher": "Contoso", "lastUpdated": "{{recent}}",
                "disk": { "uri": "{{server.BaseUrl}}images/bad.zip", "hash": "sha256:{{Sha("not the served archive"u8.ToArray())}}", "archiveRelativePath": "disk.vhdx" } },
              { "name": "Test No Hash", "publisher": "Contoso", "lastUpdated": "{{recent}}",
                "disk": { "uri": "{{server.BaseUrl}}images/test.zip", "archiveRelativePath": "disk.vhdx" } },
              { "name": "Test Future Host", "minHostVersion": "99.0.0.0",
                "disk": { "uri": "{{server.BaseUrl}}images/test.zip", "hash": "sha256:{{Sha(testZip)}}", "archiveRelativePath": "disk.vhdx" } },
              { "name": "Test Interrupt", "publisher": "Contoso", "lastUpdated": "{{recent}}",
                "disk": { "uri": "{{server.BaseUrl}}images/interrupt.zip", "hash": "sha256:{{Sha(interruptZip)}}", "archiveRelativePath": "disk.vhdx" } },
              { "name": "Test Stall", "publisher": "Contoso", "lastUpdated": "{{recent}}",
                "disk": { "uri": "{{server.BaseUrl}}images/stall.zip", "hash": "sha256:{{Sha(stallZip)}}", "archiveRelativePath": "disk.vhdx" } },
              { "name": "Test Direct VHDX", "publisher": "Contoso", "lastUpdated": "{{recent}}",
                "disk": { "uri": "{{server.BaseUrl}}images/plain.vhdx", "hash": "sha256:{{Sha(plain)}}" } },
            ], }
            """;
        var customFile = Path.Combine(temp, "custom catalog.json");
        await File.WriteAllTextAsync(customFile, catalog, new UTF8Encoding(true));

        var defaultSwitch = Guid.Parse(ImageCatalogService.DefaultSwitchId);
        using var mock = new MockHyperV(new
        {
            vms = Array.Empty<object>(),
            switches = new object[] { new { id = defaultSwitch, name = "Default Switch", type = "Internal" }, new { id = Guid.NewGuid(), name = "Lab Private", type = "Private" } },
            host = new { virtualHardDiskPath = Path.Combine(temp, "host-vhds"), virtualMachinePath = Path.Combine(temp, "host-vms") },
            vhds = Array.Empty<object>()
        });
        using var service = new ControlService();
        service.Images.StallTimeout = TimeSpan.FromSeconds(10);
        var tools = new HyperVTools(service);

        Environment.SetEnvironmentVariable(ImageCatalogService.RootVariable, null);
        var defaultStore = await service.Images.StoreAsync(default);
        check(defaultStore.Root == Path.Combine(temp, "host-vhds", "HyperVControl", "Images") && mock.Called("Get-VMHost"),
            "Without an override the library root is <Get-VMHost VirtualHardDiskPath>\\HyperVControl\\Images");
        Environment.SetEnvironmentVariable(ImageCatalogService.RootVariable, root);
        mock.ClearCalls();
        check((await service.Images.StoreAsync(default)).Root == root && !mock.Called("Get-VMHost"), "HYPERV_CONTROL_IMAGE_ROOT overrides the root without a host query");

        // Catalogs: Microsoft's feed is the default; the tests disable it so nothing leaves the machine.
        var (_, catalogs) = await Call(tools.ImageTool("catalogs"));
        var builtIn = catalogs.GetProperty("catalogs")[0];
        check(catalogs.GetProperty("catalogs").GetArrayLength() == 1 && builtIn.GetProperty("name").GetString() == "microsoft"
            && builtIn.GetProperty("location").GetString() == ImageCatalogFeed.MicrosoftLocation && builtIn.GetProperty("enabled").GetBoolean()
            && File.Exists(Path.Combine(root, "catalogs.json")), "catalogs.json starts with Microsoft's Quick Create feed");
        var (_, disabled) = await Call(tools.ImageTool("catalogs", catalogAction: "remove", catalog: "microsoft"));
        var (_, mirror) = await Call(tools.ImageTool("catalogs", catalogAction: "add", catalog: "mirror", location: server.BaseUrl + "feeds/gallery.json"));
        var (_, added) = await Call(tools.ImageTool("catalogs", catalogAction: "add", location: customFile));
        check(disabled.GetProperty("disabled").GetBoolean() && mirror.GetProperty("images").GetArrayLength() == 5
            && added.GetProperty("catalog").GetProperty("name").GetString() == "custom-catalog" && added.GetProperty("images").GetArrayLength() == 6
            && added.GetProperty("hidden").GetInt32() == 1, "Catalogs: the built-in feed is disabled, an http feed and a local UTF-8 file are added after parsing");
        var (badAdd, _) = await Call(tools.ImageTool("catalogs", catalogAction: "add", location: "relative\\gallery.json"));
        check(badAdd, "A relative catalog location is refused");

        var (_, list) = await Call(tools.ImageTool("list"));
        var images = list.GetProperty("images").EnumerateArray().ToList();
        var noHash = images.Single(i => i.GetProperty("name").GetString() == "Test No Hash");
        check(images.Count == 11 && list.GetProperty("catalogs").EnumerateArray().Count(c => c.GetProperty("status").GetString() == "ok") == 2
            && !noHash.GetProperty("fetchable").GetBoolean() && noHash.GetProperty("warnings").EnumerateArray().Any(w => w.GetString()!.StartsWith("No hash"))
            && server.Requests.All(r => r.Path.StartsWith("/feeds/")),
            "list merges catalogs with per-image warnings and reads only the feeds, never a disk URL");
        var recommended = list.GetProperty("recommended");
        var windowsImage = images.Single(i => i.GetProperty("name").GetString() == "Windows 11 dev environment");
        check(recommended.GetProperty("linux").GetProperty("name").GetString() == "Test Linux"
            && recommended.GetProperty("windows").GetProperty("name").GetString() == "Windows 11 dev environment" && recommended.GetProperty("windows").GetProperty("catalog").GetString() == "mirror"
            && !recommended.GetProperty("hint").GetString()!.Contains("expired") && recommended.GetProperty("hint").GetString()!.Contains("For Windows, fetch 'Windows 11 dev environment'")
            && windowsImage.GetProperty("recommended").GetBoolean() && windowsImage.GetProperty("fetchable").GetBoolean() && !windowsImage.TryGetProperty("expired", out _)
            && windowsImage.GetProperty("warnings").EnumerateArray().Any(w => w.GetString()!.StartsWith("Evaluation edition")),
            "The recommendation picks the newest supported Linux image and Microsoft's Windows 11 dev environment for Windows; no image is flagged expired");
        var defaultImage = list.GetProperty("defaultImage");
        check(defaultImage.GetProperty("name").GetString() == "Windows 11 dev environment" && defaultImage.GetProperty("catalog").GetString() == "mirror"
            && defaultImage.GetProperty("inLibrary").ValueKind == JsonValueKind.Null && defaultImage.GetProperty("source").GetString() == "recommended windows catalog image",
            "list names the default image: the recommended Windows catalog image, not yet in the library");

        // The default image: HYPERV_CONTROL_DEFAULT_IMAGE names a library entry or a catalog image; otherwise the recommended Windows image.
        var resolved = await service.Images.ResolveDefaultImageAsync(null, default);
        check(resolved.Entry is null && resolved.Image?.Name == "Windows 11 dev environment" && resolved.Source?.Name == "mirror"
            && resolved.Origin == ImageCatalogService.RecommendedWindowsOrigin,
            "Without HYPERV_CONTROL_DEFAULT_IMAGE the default image is the recommended Windows catalog image, to be fetched");
        Environment.SetEnvironmentVariable(ImageCatalogService.DefaultImageVariable, "test linux");
        var configured = await service.Images.ResolveDefaultImageAsync(null, default);
        var (_, configuredList) = await Call(tools.ImageTool("list"));
        Environment.SetEnvironmentVariable(ImageCatalogService.DefaultImageVariable, "No Such Image");
        var missingDefault = await Expect(() => service.Images.ResolveDefaultImageAsync(null, default));
        var (missingListFailed, missingList) = await Call(tools.ImageTool("list"));
        Environment.SetEnvironmentVariable(ImageCatalogService.DefaultImageVariable, null);
        check(configured.Entry is null && configured.Image?.Name == "Test Linux" && configured.Source?.Name == "custom-catalog" && configured.Origin == "HYPERV_CONTROL_DEFAULT_IMAGE"
            && configuredList.GetProperty("defaultImage").GetProperty("name").GetString() == "Test Linux"
            && configuredList.GetProperty("defaultImage").GetProperty("source").GetString() == "HYPERV_CONTROL_DEFAULT_IMAGE"
            && missingDefault?.ErrorType == "NotFound" && missingDefault.Message.Contains("HYPERV_CONTROL_DEFAULT_IMAGE is 'No Such Image'")
            && !missingListFailed && missingList.GetProperty("defaultImage").GetProperty("error").GetString()!.Contains("No Such Image")
            && missingList.GetProperty("defaultImage").GetProperty("name").ValueKind == JsonValueKind.Null,
            "HYPERV_CONTROL_DEFAULT_IMAGE picks a catalog image by exact, case-insensitive name; an unknown name is a clear NotFound that list reports without failing");

        // Fetch end to end: redirect, tail probe, download, hash, magic, extraction, index, cache cleanup.
        var (fetchFailed, fetched) = await Call(tools.ImageTool("fetch", imageName: "Test Linux", waitSeconds: 60));
        var entry = fetched.GetProperty("result").GetProperty("entry");
        var linuxPath = Path.Combine(root, "Test-Linux.vhdx");
        check(!fetchFailed && fetched.GetProperty("status").GetString() == "succeeded" && entry.GetProperty("path").GetString() == linuxPath
            && File.ReadAllBytes(linuxPath).AsSpan().SequenceEqual(linux) && entry.GetProperty("osFamily").GetString() == "linux"
            && entry.GetProperty("config").GetProperty("secureBoot").GetBoolean() == false && entry.GetProperty("source").GetProperty("sha256").GetString() == Sha(testZip)
            && Directory.GetFiles(Path.Combine(root, "cache")).Length == 0,
            "fetch runs as a job: verified download, extraction to <root>\\<safe name>.vhdx, library entry, archive deleted");
        check(server.Requests.Any(r => r.Path == "/go/test" && r.Range == "bytes=-65536") && server.Requests.Any(r => r.Path == "/images/test.zip" && r.Range is null),
            "The probe resolves the redirect with one 64 KB tail read before the full download");
        var library = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "library.json")))!;
        check(library["entries"]!.AsArray().Single()!["name"]!.GetValue<string>() == "Test Linux", "library.json records the entry");
        var (_, again) = await Call(tools.ImageTool("fetch", imageName: "test linux"));
        check(again.GetProperty("alreadyInLibrary").GetBoolean(), "A second fetch of the same image returns the library entry without downloading");
        Environment.SetEnvironmentVariable(ImageCatalogService.DefaultImageVariable, "TEST LINUX");
        var fromLibrary = await service.Images.ResolveDefaultImageAsync(null, default);
        Environment.SetEnvironmentVariable(ImageCatalogService.DefaultImageVariable, null);
        check(fromLibrary.Entry?.Name == "Test Linux" && fromLibrary.Image is null && fromLibrary.Origin == "HYPERV_CONTROL_DEFAULT_IMAGE",
            "HYPERV_CONTROL_DEFAULT_IMAGE naming a library entry resolves to that entry before any catalog");

        // An interrupted download keeps its partial file; the next fetch resumes it with a Range request.
        var (interruptFailed, firstTry) = await Call(tools.ImageTool("fetch", imageName: "Test Interrupt", waitSeconds: 60));
        var partial = Path.Combine(root, "cache", Sha(interruptZip) + ".zip.partial");
        var kept = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        check(interruptFailed && firstTry.GetProperty("errorType").GetString() == "DownloadInterrupted" && kept is > 0 and < 3_000_000,
            $"A dropped connection fails the job with DownloadInterrupted and keeps the partial download ({kept} bytes)");
        var (resumeFailed, resumed) = await Call(tools.ImageTool("fetch", imageName: "Test Interrupt", waitSeconds: 60));
        check(!resumeFailed && resumed.GetProperty("result").GetProperty("resumedFromBytes").GetInt64() == kept
            && server.Requests.Any(r => r.Path == "/images/interrupt.zip" && r.Range == $"bytes={kept}-")
            && File.ReadAllBytes(Path.Combine(root, "Test-Interrupt.vhdx")).AsSpan().SequenceEqual(interrupted) && !File.Exists(partial),
            $"The next fetch resumes with Range: bytes={kept}- and produces the verified image");

        // Cancelling a running fetch deletes its partial download and extraction output.
        var (_, stalling) = await Call(tools.ImageTool("fetch", imageName: "Test Stall"));
        var stallJob = stalling.GetProperty("id").GetString()!;
        await server.StallReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var stallPartial = Path.Combine(root, "cache", Sha(stallZip) + ".zip.partial");
        for (var i = 0; i < 200 && !(service.Jobs.Get(stallJob)?.Progress.BytesDone > 0); i++) await Task.Delay(25);
        var hadPartial = File.Exists(stallPartial) && service.Jobs.Get(stallJob)?.Progress.Message == "downloading";
        var (_, cancelled) = await Call(tools.JobsTool("cancel", stallJob, 30));
        server.ReleaseStall();
        check(hadPartial && cancelled.GetProperty("status").GetString() == "cancelled" && !File.Exists(stallPartial)
            && !Directory.EnumerateFiles(root, "Test-Stall*").Any() && !File.ReadAllText(Path.Combine(root, "library.json")).Contains("Test Stall"),
            "hyperv_jobs cancel stops the download and deletes the partial file; nothing is registered");

        var (mismatchFailed, mismatch) = await Call(tools.ImageTool("fetch", imageName: "Test Bad Hash", waitSeconds: 60));
        check(mismatchFailed && mismatch.GetProperty("errorType").GetString() == "HashMismatch"
            && Directory.GetFiles(Path.Combine(root, "cache")).Length == 0 && !Directory.EnumerateFiles(root, "Test-Bad-Hash*").Any(),
            "A SHA-256 mismatch fails the job and deletes the download");
        var (noHashFailed, refused) = await Call(tools.ImageTool("fetch", imageName: "Test No Hash"));
        var (hiddenFailed, hidden) = await Call(tools.ImageTool("fetch", imageName: "Test Future Host"));
        check(noHashFailed && refused.GetProperty("errorType").GetString() == "UnsupportedImage"
            && hiddenFailed && hidden.GetProperty("error").GetString()!.Contains("hidden by their host version limits"),
            "Unverifiable and host-incompatible images are refused before any job starts");
        var (directFailed, direct) = await Call(tools.ImageTool("fetch", imageName: "Test Direct VHDX", waitSeconds: 60));
        var directPath = Path.Combine(root, "Test-Direct-VHDX.vhdx");
        check(!directFailed && File.ReadAllBytes(directPath).AsSpan().SequenceEqual(plain) && direct.GetProperty("result").GetProperty("entry").GetProperty("osFamily").GetString() == "other",
            "A .vhdx disk is verified and moved into the library without a copy");

        // Register adopts files in place; templates become read-only; guards refuse bad input.
        var template = Path.Combine(user, "Win Template.vhdx");
        await File.WriteAllBytesAsync(template, Payload(7, 200_000));
        var (registerFailed, registered) = await Call(tools.ImageTool("register", path: template, name: "Win Template", osFamily: "windows", kind: "template"));
        check(!registerFailed && registered.GetProperty("entry").GetProperty("readOnly").GetBoolean() && File.GetAttributes(template).HasFlag(FileAttributes.ReadOnly)
            && registered.GetProperty("entry").GetProperty("path").GetString() == template, "register adopts a .vhdx in place and makes a template read-only");
        var notVhdx = Path.Combine(user, "not.vhdx");
        await File.WriteAllTextAsync(notVhdx, "plain text");
        var linuxPlain = Path.Combine(user, "linux.vhdx");
        await File.WriteAllBytesAsync(linuxPlain, Payload(8, 100_000));
        var (badMagic, badMagicResult) = await Call(tools.ImageTool("register", path: notVhdx, name: "Not a disk", osFamily: "linux"));
        var (duplicate, duplicateResult) = await Call(tools.ImageTool("register", path: linuxPlain, name: "win template", osFamily: "linux"));
        var (samePath, samePathResult) = await Call(tools.ImageTool("register", path: template, name: "Another name", osFamily: "windows"));
        var (relative, _) = await Call(tools.ImageTool("register", path: "disk.vhdx", name: "Relative", osFamily: "linux"));
        check(badMagic && badMagicResult.GetProperty("errorType").GetString() == "UnsupportedImage"
            && duplicate && duplicateResult.GetProperty("errorType").GetString() == "AlreadyExists"
            && samePath && samePathResult.GetProperty("errorType").GetString() == "AlreadyExists" && relative,
            "register refuses files without the VHDX signature, duplicate names or files, and relative paths");
        var (_, inferred) = await Call(tools.ImageTool("register", path: linuxPlain, name: "Ubuntu Plain"));
        check(inferred.GetProperty("entry").GetProperty("osFamily").GetString() == "linux" && inferred.GetProperty("warnings").GetArrayLength() == 1,
            "register infers a missing osFamily from the name and says so");
        var iso = Path.Combine(user, "setup.iso");
        var isoBytes = new byte[0x9000];
        "CD001"u8.CopyTo(isoBytes.AsSpan(0x8001));
        await File.WriteAllBytesAsync(iso, isoBytes);
        var (_, isoEntry) = await Call(tools.ImageTool("register", path: iso, name: "Setup ISO", osFamily: "windows"));
        check(isoEntry.GetProperty("entry").GetProperty("format").GetString() == "iso", "register adopts an ISO as installation media");

        // The in-use guard: attached disks, DVDs and differencing parents (also through checkpoints) block removal.
        var child = Path.Combine(user, "child.avhdx");
        var attachedId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        EditState(mock, state =>
        {
            state["vms"]!.AsArray().Add(Vm(attachedId, "Lab Attached", [template]));
            var parentVm = Vm(parentId, "Lab Checkpointed", []);
            parentVm["snapshots"] = new JsonArray(new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["name"] = "base", ["creationTime"] = "2026-01-01T00:00:00Z", ["disks"] = new JsonArray(child) });
            parentVm["dvd"] = iso;
            state["vms"]!.AsArray().Add(parentVm);
            state["vhds"] = new JsonArray(new JsonObject { ["path"] = child, ["parentPath"] = linuxPlain });
        });
        var (attachedFailed, attached) = await Call(tools.ImageTool("remove", name: "Win Template"));
        var (parentFailed, parent) = await Call(tools.ImageTool("remove", name: "Ubuntu Plain", deleteFile: true));
        var (dvdFailed, dvd) = await Call(tools.ImageTool("remove", name: "Setup ISO", deleteFile: true));
        check(attachedFailed && attached.GetProperty("errorType").GetString() == "InUse" && Relation(attached) == "attached"
            && parentFailed && Relation(parent) == "differencingParent" && dvdFailed && Relation(dvd) == "attachedDvd"
            && File.Exists(linuxPlain) && File.Exists(iso),
            "remove refuses an attached disk, a differencing parent reached through a checkpoint, and a mounted ISO");
        EditState(mock, state =>
        {
            state["vms"] = new JsonArray();
            state["vhds"] = new JsonArray();
        });
        var (_, removedIso) = await Call(tools.ImageTool("remove", name: "Setup ISO", deleteFile: true));
        var spare = Path.Combine(user, "spare.vhdx");
        await File.WriteAllBytesAsync(spare, Payload(9, 50_000));
        await Call(tools.ImageTool("register", path: spare, name: "Spare Template", osFamily: "windows", kind: "template"));
        var (_, removedSpare) = await Call(tools.ImageTool("remove", name: "Spare Template", deleteFile: true));
        check(!File.Exists(iso) && removedIso.GetProperty("deleted").GetArrayLength() == 1 && !File.Exists(spare) && removedSpare.GetProperty("deleted").GetArrayLength() == 1,
            "remove with deleteFile deletes an unused image, clearing a template's read-only attribute first");
        var media = Path.Combine(user, "media.iso");
        await File.WriteAllBytesAsync(media, isoBytes);
        await Call(tools.ImageTool("register", path: media, name: "Install Media", osFamily: "linux"));
        var (isoDeploy, isoDeployResult) = await Call(tools.ImageTool("deploy", imageName: "Install Media", vmName: "Lab Media", vmPath: vms));
        check(isoDeploy && isoDeployResult.GetProperty("error").GetString()!.Contains("installation media") && !Directory.Exists(Path.Combine(vms, "Lab Media")),
            "deploy refuses an ISO entry before touching the host");

        // Deploy: per-VM disk, then the VM through the lifecycle create path, rendered against the mock.
        mock.ClearCalls();
        var (winFailed, win) = await Call(tools.ImageTool("deploy", imageName: "Win Template", vmName: "Lab Win", vmPath: vms, notes: "lab notes", memoryMb: 6144, processors: 3));
        var calls = mock.Calls();
        var winDisk = Path.Combine(vms, "Lab Win", "Virtual Hard Disks", "Lab Win.vhdx");
        var newVhd = calls.Single(c => c.Cmdlet == "New-VHD").Parameters;
        var newVm = calls.Single(c => c.Cmdlet == "New-VM").Parameters;
        var setVm = calls.Single(c => c.Cmdlet == "Set-VM").Parameters;
        var setMemory = calls.Single(c => c.Cmdlet == "Set-VMMemory").Parameters;
        var firmwareCalls = calls.Where(c => c.Cmdlet == "Set-VMFirmware").Select(c => c.Parameters).ToList();
        check(!winFailed && newVhd.GetProperty("Path").GetString() == winDisk && newVhd.GetProperty("ParentPath").GetString() == template && newVhd.GetProperty("Differencing").GetBoolean()
            && newVm.GetProperty("VHDPath").GetString() == winDisk && newVm.GetProperty("Path").GetString() == vms && newVm.GetProperty("Generation").GetInt32() == 2
            && newVm.GetProperty("MemoryStartupBytes").GetInt64() == 6144L << 20 && newVm.GetProperty("SwitchName").GetString() == "Default Switch"
            && calls.Any(c => c.Cmdlet == "Set-VMProcessor" && c.Parameters.GetProperty("Count").GetInt32() == 3),
            "Linked deploy creates a differencing child under the VM folder and the VM on the Default Switch (resolved by ID)");
        check(!setMemory.GetProperty("DynamicMemoryEnabled").GetBoolean() && !setVm.GetProperty("AutomaticCheckpointsEnabled").GetBoolean()
            && setVm.GetProperty("CheckpointType").GetString() == "Standard" && setVm.GetProperty("Notes").GetString() == "lab notes" && !setVm.TryGetProperty("EnhancedSessionTransportType", out _)
            && firmwareCalls.Any(f => f.TryGetProperty("SecureBootTemplate", out var t) && t.GetString() == "MicrosoftWindows")
            && firmwareCalls.Any(f => f.TryGetProperty("EnableSecureBoot", out var e) && e.GetString() == "On")
            && calls.Any(c => c.Cmdlet == "Set-VMKeyProtector" && c.Parameters.GetProperty("NewLocalKeyProtector").GetBoolean()) && mock.Called("Enable-VMTPM") && !mock.Called("Start-VM"),
            "Windows lab defaults: static memory, no automatic checkpoints, Standard checkpoints, MicrosoftWindows Secure Boot, a local key protector and a vTPM");
        int Index(string cmdlet) => calls.ToList().FindIndex(c => c.Cmdlet == cmdlet);
        check(Index("New-VHD") < Index("New-VM") && Index("New-VM") < Index("Set-VM") && Index("Set-VM") < Index("Set-VMFirmware")
            && Index("Set-VMFirmware") < Index("Set-VMKeyProtector") && Index("Set-VMKeyProtector") < Index("Enable-VMTPM"),
            "Order: New-VHD, New-VM, Set-VM, Set-VMFirmware, Set-VMKeyProtector, Enable-VMTPM (template before the vTPM locks it)");
        var winLibrary = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "library.json")))!["entries"]!.AsArray().Single(e => e!["name"]!.GetValue<string>() == "Win Template")!;
        check(win.GetProperty("disk").GetProperty("type").GetString() == "differencing" && win.GetProperty("disk").GetProperty("parentPath").GetString() == template
            && win.GetProperty("vm").GetProperty("Name").GetString() == "Lab Win" && win.GetProperty("vm").GetProperty("Firmware").ValueKind == JsonValueKind.Object
            && win.GetProperty("vm").GetProperty("Security").ValueKind == JsonValueKind.Object && winLibrary["deployments"]!.AsArray().Count == 1
            && winLibrary["deployments"]![0]!["diskPath"]!.GetValue<string>() == winDisk,
            "Deploy returns the VM with firmware and security, the child disk and its parent, and records the deployment in library.json");

        mock.ClearCalls();
        var (linuxFailed, linuxVm) = await Call(tools.ImageTool("deploy", imageName: "Test Linux", vmName: "Lab Linux", vmPath: vms, start: true, switchName: "Lab Private"));
        calls = mock.Calls();
        var linuxFirmware = calls.Where(c => c.Cmdlet == "Set-VMFirmware").Select(c => c.Parameters).ToList();
        check(!linuxFailed && linuxFirmware.Any(f => f.TryGetProperty("EnableSecureBoot", out var e) && e.GetString() == "Off")
            && calls.Single(c => c.Cmdlet == "Set-VM").Parameters.GetProperty("EnhancedSessionTransportType").GetString() == "HvSocket"
            && !mock.Called("Enable-VMTPM") && !mock.Called("Set-VMKeyProtector") && mock.Called("Start-VM") && linuxVm.GetProperty("started").GetBoolean()
            && calls.Single(c => c.Cmdlet == "New-VM").Parameters.GetProperty("SwitchName").GetString() == "Lab Private"
            && File.GetAttributes(linuxPath).HasFlag(FileAttributes.ReadOnly),
            "Linux lab defaults follow the catalog: Secure Boot off, HvSocket, no vTPM, start on request; the parent becomes read-only");

        mock.ClearCalls();
        var (_, plainVm) = await Call(tools.ImageTool("deploy", imageName: "Ubuntu Plain", vmName: "Lab Plain", vmPath: vms));
        var plainFirmware = mock.Calls().Where(c => c.Cmdlet == "Set-VMFirmware").Select(c => c.Parameters).ToList();
        check(plainFirmware.Any(f => f.TryGetProperty("EnableSecureBoot", out var e) && e.GetString() == "On")
            && plainFirmware.Any(f => f.TryGetProperty("SecureBootTemplate", out var t) && t.GetString() == "MicrosoftUEFICertificateAuthority")
            && plainVm.GetProperty("disk").GetProperty("type").GetString() == "differencing",
            "Linux without a catalog setting keeps Secure Boot on with the Microsoft UEFI CA template");

        mock.ClearCalls();
        var (copyFailed, copied) = await Call(tools.ImageTool("deploy", imageName: "Test Direct VHDX", vmName: "Lab Copy", vmPath: vms, linked: false));
        var copyDisk = Path.Combine(vms, "Lab Copy", "Virtual Hard Disks", "Lab Copy.vhdx");
        check(!copyFailed && !mock.Called("New-VHD") && File.ReadAllBytes(copyDisk).AsSpan().SequenceEqual(plain) && !File.GetAttributes(copyDisk).HasFlag(FileAttributes.ReadOnly)
            && mock.Calls().Any(c => c.Cmdlet == "Set-VMFirmware" && c.Parameters.TryGetProperty("EnableSecureBoot", out var e) && e.GetString() == "Off")
            && copied.GetProperty("warnings").EnumerateArray().Any(w => w.GetString()!.Contains("'other'")),
            "A full-copy deploy copies the image into the VM folder; an unknown OS family gets Secure Boot off with a warning");

        mock.ClearCalls();
        var (duplicateVm, duplicateVmResult) = await Call(tools.ImageTool("deploy", imageName: "Win Template", vmName: "Lab Win", vmPath: Path.Combine(temp, "elsewhere")));
        check(duplicateVm && duplicateVmResult.GetProperty("errorType").GetString() == "InvalidArgument" && !mock.Called("New-VHD") && !mock.Called("New-VM")
            && !Directory.Exists(Path.Combine(temp, "elsewhere", "Lab Win")),
            "A duplicate VM name is refused before any disk is created, and the empty folder is removed");

        var (inUse, inUseResult) = await Call(tools.ImageTool("remove", name: "Win Template", deleteFile: true));
        check(inUse && Relation(inUseResult) is "differencingParent" or "attached" && File.Exists(template),
            "A template with a deployed linked clone cannot be removed");

        await QuickCreateScenarios(check, temp, server, service, tools, mock, root, vms, template, Sha(testZip));

        // hyperv_jobs: list, status, wait, unknown IDs.
        var (_, jobs) = await Call(tools.JobsTool());
        var statuses = jobs.GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("status").GetString()).ToList();
        var (_, status) = await Call(tools.JobsTool("status", stallJob));
        var (_, waited) = await Call(tools.JobsTool("wait", fetched.GetProperty("id").GetString(), 1));
        var (unknown, unknownResult) = await Call(tools.JobsTool("status", "nope"));
        var (badWait, _) = await Call(tools.JobsTool("wait", stallJob, 7200));
        check(statuses.Count >= 6 && statuses.Contains("succeeded") && statuses.Contains("failed") && statuses.Contains("cancelled")
            && status.GetProperty("status").GetString() == "cancelled" && status.GetProperty("errorType").GetString() == "Cancelled"
            && waited.GetProperty("status").GetString() == "succeeded" && waited.GetProperty("progress").GetProperty("percent").GetDouble() == 100
            && unknown && unknownResult.GetProperty("errorType").GetString() == "ArgumentException" && badWait,
            "hyperv_jobs lists, reports and waits on jobs with camelCase statuses and rejects unknown IDs and unbounded waits");
    }

    /// <summary>
    /// hyperv_vm_create's default source=image against local fixtures only: the mirror of Microsoft's feed is removed first, so the
    /// default image is a small local Windows evaluation fixture. Covers refusals before any job or download, the fetch-then-deploy
    /// job, the in-call library deploy, imageName naming a catalog image, HYPERV_CONTROL_DEFAULT_IMAGE (by name and by SHA-256),
    /// allowDuplicateName, and a running fetch that later calls join instead of downloading again.
    /// </summary>
    private static async Task QuickCreateScenarios(Action<bool, string> check, string temp, FixtureServer server, ControlService service, HyperVTools tools,
        MockHyperV mock, string root, string vms, string template, string testLinuxSha)
    {
        var quickWin = Payload(10, 400_000);
        var quickWinZip = Zip(("Images/Quick.vhdx", quickWin));
        var namedZip = Zip(("named.vhdx", Payload(11, 300_000)));
        var holdZip = Zip(("hold.vhdx", Payload(12, 2_000_000)));
        server.Add("/images/quick-win.zip", quickWinZip);
        server.Add("/images/quick-named.zip", namedZip);
        server.Add("/images/hold.zip", holdZip);
        server.Hold("/images/hold.zip", 500_000);
        var recent = DateTimeOffset.UtcNow.AddDays(-3).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var quickCatalog = Path.Combine(temp, "quick catalog.json");
        await File.WriteAllTextAsync(quickCatalog, $$"""
            { "images": [
              { "name": "Test Windows Quick", "publisher": "Microsoft", "version": "10.0.26100", "lastUpdated": "{{recent}}",
                "description": "Synthetic evaluation copy of Windows for quick create tests.", "config": { "secureBoot": "true" },
                "details": [ { "name": "Edition", "value": "Windows 11 Enterprise" } ],
                "disk": { "uri": "{{server.BaseUrl}}images/quick-win.zip", "hash": "sha256:{{Sha(quickWinZip)}}", "archiveRelativePath": "Images/Quick.vhdx" } },
              { "name": "Test Quick Named", "publisher": "Contoso", "lastUpdated": "{{recent}}", "config": { "secureBoot": "false" },
                "disk": { "uri": "{{server.BaseUrl}}images/quick-named.zip", "hash": "sha256:{{Sha(namedZip)}}", "archiveRelativePath": "named.vhdx" } },
              { "name": "Test Quick Hold", "publisher": "Contoso", "lastUpdated": "{{recent}}",
                "disk": { "uri": "{{server.BaseUrl}}images/hold.zip", "hash": "sha256:{{Sha(holdZip)}}", "archiveRelativePath": "hold.vhdx" } },
              { "name": "Test Linux Copy", "publisher": "Canonical Group Ltd", "lastUpdated": "{{recent}}",
                "disk": { "uri": "{{server.BaseUrl}}go/test", "hash": "sha256:{{testLinuxSha}}", "archiveRelativePath": "disk/TEST-image.vhdx" } }
            ] }
            """, new UTF8Encoding(false));
        // Only local fixtures from here on: the mirror's Windows 11 dev environment points at Microsoft's real 23 GB download.
        var (_, removedMirror) = await Call(tools.ImageTool("catalogs", catalogAction: "remove", catalog: "mirror"));
        var (_, quickAdded) = await Call(tools.ImageTool("catalogs", catalogAction: "add", catalog: "quick", location: quickCatalog));
        var (_, before) = await Call(tools.ImageTool("list"));
        var quickDefault = before.GetProperty("defaultImage");
        check(removedMirror.GetProperty("removed").GetBoolean() && quickAdded.GetProperty("images").GetArrayLength() == 4
            && quickDefault.GetProperty("name").GetString() == "Test Windows Quick" && quickDefault.GetProperty("catalog").GetString() == "quick"
            && quickDefault.GetProperty("inLibrary").ValueKind == JsonValueKind.Null && quickDefault.GetProperty("source").GetString() == "recommended windows catalog image",
            "The default image follows the recommendation to the newest Windows catalog image, here a local evaluation fixture");

        // Refusals that need no image come before any job or download.
        var jobsBefore = service.Jobs.List().Count;
        var (tooLong, tooLongResult) = await Call(tools.CreateVm("Lab Refused", waitSeconds: 3601));
        var (negative, _) = await Call(tools.CreateVm("Lab Refused", waitSeconds: -1));
        var (otherSource, otherSourceResult) = await Call(tools.CreateVm("Lab Refused", diskGb: 10, isoPath: Path.Combine(temp, "setup.iso")));
        var (relative, _) = await Call(tools.CreateVm("Lab Refused", path: "relative"));
        var (badName, _) = await Call(tools.CreateVm("bad/name", path: vms));
        var (misplacedCatalog, _) = await Call(tools.CreateVm("Lab Refused", source: "none", catalog: "quick"));
        var (unknownSource, unknownSourceResult) = await Call(tools.CreateVm("Lab Refused", source: "Image"));
        var (diskTaken, diskTakenResult) = await Call(tools.CreateVm("Lab Win", imageName: "Test Quick Named", path: vms));
        var elsewhere = Path.Combine(temp, "preflight");
        var (duplicate, duplicateResult) = await Call(tools.CreateVm("Lab Win", imageName: "Test Quick Named", path: elsewhere));
        var (noSwitch, noSwitchResult) = await Call(tools.CreateVm("Lab No Switch", imageName: "Test Quick Named", path: elsewhere, switchName: "Nope"));
        var (deployFailed, deployResult) = await Call(tools.ImageTool("deploy", vmName: "Lab Deploy Default", vmPath: vms));
        check(tooLong && tooLongResult.GetProperty("error").GetString()!.Contains("waitSeconds") && negative
            && otherSource && otherSourceResult.GetProperty("error").GetString()!.Contains("does not use isoPath, diskGb") && relative && badName && misplacedCatalog
            && unknownSource && unknownSourceResult.GetProperty("error").GetString()!.Contains("image (the default), new_disk")
            && diskTaken && diskTakenResult.GetProperty("errorType").GetString() == "AlreadyExists"
            && duplicate && duplicateResult.GetProperty("errorType").GetString() == "InvalidArgument" && duplicateResult.GetProperty("error").GetString()!.Contains("A VM named 'Lab Win' already exists")
            && noSwitch && noSwitchResult.GetProperty("errorType").GetString() == "NotFound" && !Directory.Exists(elsewhere)
            && deployFailed && deployResult.GetProperty("errorType").GetString() == "NotFound" && deployResult.GetProperty("error").GetString()!.Contains("hyperv_vm_create")
            && service.Jobs.List().Count == jobsBefore && !server.Requests.Any(r => r.Path.StartsWith("/images/quick", StringComparison.Ordinal)),
            "waitSeconds, an unknown source, other sources' parameters, bad paths and names, an existing disk, a duplicate VM name and a missing switch are refused before any job or download; deploy without imageName points at hyperv_vm_create");

        // The default image is not in the library: one job fetches it and then deploys it; the job's result is the VM.
        mock.ClearCalls();
        var (quickFailed, quick) = await Call(tools.CreateVm("Lab Quick", path: vms, notes: "quick notes"));
        var quickPath = Path.Combine(root, "Test-Windows-Quick.vhdx");
        var quickCalls = mock.Calls();
        var quickResult = quick.GetProperty("result");
        check(!quickFailed && quick.GetProperty("name").GetString() == "quick create Lab Quick" && quick.GetProperty("status").GetString() == "succeeded"
            && quick.GetProperty("progress").GetProperty("message").GetString() == "done" && quick.GetProperty("progress").GetProperty("percent").GetDouble() == 100
            && quickResult.GetProperty("vm").GetProperty("Name").GetString() == "Lab Quick" && quickResult.GetProperty("disk").GetProperty("type").GetString() == "differencing"
            && quickResult.GetProperty("parent").GetProperty("name").GetString() == "Test Windows Quick" && quickResult.GetProperty("parent").GetProperty("path").GetString() == quickPath
            && File.ReadAllBytes(quickPath).AsSpan().SequenceEqual(quickWin) && File.GetAttributes(quickPath).HasFlag(FileAttributes.ReadOnly)
            && quickCalls.Single(c => c.Cmdlet == "New-VHD").Parameters.GetProperty("ParentPath").GetString() == quickPath
            && quickCalls.Single(c => c.Cmdlet == "Set-VM").Parameters.GetProperty("Notes").GetString() == "quick notes" && quickCalls.Any(c => c.Cmdlet == "Enable-VMTPM")
            && quickResult.GetProperty("warnings").EnumerateArray().Any(w => w.GetString()!.StartsWith("Evaluation edition: Windows 11 Enterprise Evaluation"))
            && server.Requests.Count(r => r.Path == "/images/quick-win.zip" && r.Range is null) == 1,
            "hyperv_vm_create without source fetches the default image in one quick-create job, then deploys a Windows linked clone; the job's result is the VM");

        mock.ClearCalls();
        var (secondFailed, second) = await Call(tools.CreateVm("Lab Quick 2", path: vms, start: true));
        var (_, deployedDefault) = await Call(tools.ImageTool("deploy", vmName: "Lab Deploy Default", vmPath: vms));
        var (_, after) = await Call(tools.ImageTool("list"));
        check(!secondFailed && !second.TryGetProperty("status", out _) && second.GetProperty("vm").GetProperty("Name").GetString() == "Lab Quick 2"
            && second.GetProperty("disk").GetProperty("parentPath").GetString() == quickPath && second.GetProperty("started").GetBoolean()
            && deployedDefault.GetProperty("parent").GetProperty("name").GetString() == "Test Windows Quick"
            && after.GetProperty("defaultImage").GetProperty("inLibrary").GetString() == "Test Windows Quick"
            && server.Requests.Count(r => r.Path == "/images/quick-win.zip" && r.Range is null) == 1,
            "With the default image in the library, hyperv_vm_create without source and hyperv_image deploy without imageName are in-call linked clones; nothing downloads again");

        // imageName naming a catalog image takes the same job path, with that image's settings.
        mock.ClearCalls();
        var (namedFailed, named) = await Call(tools.CreateVm("Lab Named", imageName: "test quick named", catalog: "quick", path: vms));
        var namedPath = Path.Combine(root, "Test-Quick-Named.vhdx");
        check(!namedFailed && named.GetProperty("name").GetString() == "quick create Lab Named" && named.GetProperty("status").GetString() == "succeeded"
            && named.GetProperty("result").GetProperty("vm").GetProperty("Name").GetString() == "Lab Named"
            && named.GetProperty("result").GetProperty("parent").GetProperty("path").GetString() == namedPath && File.Exists(namedPath)
            && mock.Calls().Any(c => c.Cmdlet == "Set-VMFirmware" && c.Parameters.TryGetProperty("EnableSecureBoot", out var e) && e.GetString() == "Off") && !mock.Called("Enable-VMTPM"),
            "imageName naming a catalog image (catalog disambiguates) runs the same fetch-then-deploy job with the image's own settings");

        // HYPERV_CONTROL_DEFAULT_IMAGE: a library entry by name, or a catalog image matched to the library by SHA-256.
        Environment.SetEnvironmentVariable(ImageCatalogService.DefaultImageVariable, "Win Template");
        mock.ClearCalls();
        var (envFailed, envVm) = await Call(tools.CreateVm("Lab Env Default", path: vms));
        var envCalls = mock.Calls();
        Environment.SetEnvironmentVariable(ImageCatalogService.DefaultImageVariable, "Test Linux Copy");
        var bySha = await service.Images.ResolveDefaultImageAsync(null, default);
        Environment.SetEnvironmentVariable(ImageCatalogService.DefaultImageVariable, null);
        check(!envFailed && envVm.GetProperty("parent").GetProperty("name").GetString() == "Win Template"
            && envCalls.Single(c => c.Cmdlet == "New-VHD").Parameters.GetProperty("ParentPath").GetString() == template
            && envCalls.Any(c => c.Cmdlet == "Set-VMFirmware" && c.Parameters.TryGetProperty("SecureBootTemplate", out var t) && t.GetString() == "MicrosoftWindows")
            && bySha.Entry?.Name == "Test Linux" && bySha.Image?.Name == "Test Linux Copy" && bySha.Origin == "HYPERV_CONTROL_DEFAULT_IMAGE",
            "HYPERV_CONTROL_DEFAULT_IMAGE naming a library entry makes hyperv_vm_create without source a linked clone of it; a catalog image with a library entry's SHA-256 resolves to that entry");

        mock.ClearCalls();
        var (twinFailed, twin) = await Call(tools.CreateVm("Lab Quick", imageName: "Test Windows Quick", path: Path.Combine(temp, "twins"), allowDuplicateName: true));
        check(!twinFailed && mock.Called("New-VM") && twin.GetProperty("warnings").EnumerateArray().Any(w => w.GetString()!.Contains("already has this name")),
            "allowDuplicateName reaches the image path's create, which warns instead of refusing");

        // A running fetch is joined, never repeated: hyperv_image fetch and a second quick create wait for the first job's download.
        service.Images.StallTimeout = TimeSpan.FromSeconds(60);
        var (_, joinA) = await Call(tools.CreateVm("Lab Join A", imageName: "Test Quick Hold", path: vms, waitSeconds: 0));
        var jobA = joinA.GetProperty("id").GetString()!;
        await server.HoldReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var (_, fetchDuring) = await Call(tools.ImageTool("fetch", imageName: "Test Quick Hold"));
        var (_, joinB) = await Call(tools.CreateVm("Lab Join B", imageName: "Test Quick Hold", path: vms, waitSeconds: 0));
        var jobB = joinB.GetProperty("id").GetString()!;
        for (var i = 0; i < 200 && service.Jobs.Get(jobB)?.Progress.Message?.Contains(jobA) != true; i++) await Task.Delay(25);
        var mirrored = service.Jobs.Get(jobB)?.Progress.Message ?? "";
        server.ReleaseHold();
        var (_, doneA) = await Call(tools.JobsTool("wait", jobA, 60));
        var (_, doneB) = await Call(tools.JobsTool("wait", jobB, 60));
        service.Images.StallTimeout = TimeSpan.FromSeconds(10);
        var holdPath = Path.Combine(root, "Test-Quick-Hold.vhdx");
        check(joinA.GetProperty("status").GetString() is "queued" or "running" && joinA.GetProperty("next").GetString() == "hyperv_jobs action=wait jobId=" + jobA
            && fetchDuring.GetProperty("alreadyRunning").GetBoolean() && fetchDuring.GetProperty("job").GetProperty("id").GetString() == jobA
            && jobB != jobA && joinB.GetProperty("name").GetString() == "quick create Lab Join B" && joinB.GetProperty("next").GetString() == "hyperv_jobs action=wait jobId=" + jobB
            && mirrored.StartsWith($"job {jobA} (quick create Lab Join A) is fetching 'Test Quick Hold'")
            && doneA.GetProperty("status").GetString() == "succeeded" && doneA.GetProperty("result").GetProperty("vm").GetProperty("Name").GetString() == "Lab Join A"
            && doneB.GetProperty("status").GetString() == "succeeded" && doneB.GetProperty("result").GetProperty("vm").GetProperty("Name").GetString() == "Lab Join B"
            && doneA.GetProperty("result").GetProperty("parent").GetProperty("path").GetString() == holdPath
            && doneB.GetProperty("result").GetProperty("parent").GetProperty("path").GetString() == holdPath
            && server.Requests.Count(r => r.Path == "/images/hold.zip" && r.Range is null) == 1,
            "A running fetch is joined, not repeated: hyperv_image fetch returns the quick-create job, and a second quick create waits for its single download, then creates its own VM");
    }

    private static async Task<HyperVOperationException?> Expect(Func<Task> action)
    {
        try { await action(); return null; }
        catch (HyperVOperationException ex) { return ex; }
    }

    private static string? Relation(JsonElement result) =>
        result.TryGetProperty("references", out var references) && references.GetArrayLength() > 0 ? references[0].GetProperty("relation").GetString() : null;

    private static JsonObject Vm(Guid id, string name, string[] disks) => new()
    {
        ["id"] = id.ToString(), ["name"] = name, ["state"] = "Off", ["generation"] = 2, ["processorCount"] = 2, ["memoryStartup"] = 4294967296L,
        ["dynamicMemory"] = false, ["disks"] = new JsonArray(disks.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()), ["snapshots"] = new JsonArray()
    };

    private static void EditState(MockHyperV mock, Action<JsonObject> edit)
    {
        var state = JsonNode.Parse(File.ReadAllText(mock.StatePath))!.AsObject();
        edit(state);
        File.WriteAllText(mock.StatePath, state.ToJsonString(), new UTF8Encoding(false));
    }

    private static async Task<(bool IsError, JsonElement Json)> Call(Task<CallToolResult> call)
    {
        var result = await call;
        using var document = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
        return (result.IsError == true, document.RootElement.Clone());
    }

    /// <summary>Deterministic, incompressible bytes that start with the VHDX signature.</summary>
    private static byte[] Payload(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        "vhdxfile"u8.CopyTo(bytes);
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var (name, data) in entries)
            {
                using var stream = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
                stream.Write(data);
            }
        return memory.ToArray();
    }

    /// <summary>A stored single-entry archive whose sizes and offset exist only in Zip64 records, as in multi-GB images.</summary>
    private static byte[] Zip64(string name, byte[] data)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var crc = Crc32(data);
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write(0x04034b50u); writer.Write((ushort)45); writer.Write((ushort)0x0800); writer.Write((ushort)0); writer.Write(0u);
        writer.Write(crc); writer.Write(0xFFFFFFFFu); writer.Write(0xFFFFFFFFu); writer.Write((ushort)nameBytes.Length); writer.Write((ushort)20);
        writer.Write(nameBytes); writer.Write((ushort)1); writer.Write((ushort)16); writer.Write((long)data.Length); writer.Write((long)data.Length);
        writer.Write(data);
        var central = memory.Position;
        writer.Write(0x02014b50u); writer.Write((ushort)45); writer.Write((ushort)45); writer.Write((ushort)0x0800); writer.Write((ushort)0); writer.Write(0u);
        writer.Write(crc); writer.Write(0xFFFFFFFFu); writer.Write(0xFFFFFFFFu); writer.Write((ushort)nameBytes.Length); writer.Write((ushort)28); writer.Write((ushort)0);
        writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0u); writer.Write(0xFFFFFFFFu);
        writer.Write(nameBytes); writer.Write((ushort)1); writer.Write((ushort)24); writer.Write((long)data.Length); writer.Write((long)data.Length); writer.Write(0L);
        var centralSize = memory.Position - central;
        var record = memory.Position;
        writer.Write(0x06064b50u); writer.Write(44L); writer.Write((ushort)45); writer.Write((ushort)45); writer.Write(0u); writer.Write(0u);
        writer.Write(1L); writer.Write(1L); writer.Write(centralSize); writer.Write(central);
        writer.Write(0x07064b50u); writer.Write(0u); writer.Write(record); writer.Write(1u);
        writer.Write(0x06054b50u); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0xFFFF); writer.Write((ushort)0xFFFF);
        writer.Write(0xFFFFFFFFu); writer.Write(0xFFFFFFFFu); writer.Write((ushort)0);
        writer.Flush();
        return memory.ToArray();
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}

/// <summary>
/// A loopback HTTP server for image fixtures: byte ranges (suffix, open and closed), redirects, a connection
/// dropped once mid-download, a download that stalls until released (then breaks), and a download that holds
/// until released (then completes). It logs every request's path and Range.
/// </summary>
internal sealed class FixtureServer : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> redirects = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource stallRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource holdRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task loop;
    private string? dropPath, stallPath, holdPath;
    private long dropAfter, stallAfter, holdAfter;
    private int dropped;

    public ConcurrentQueue<(string Path, string? Range)> Requests { get; } = new();
    public TaskCompletionSource StallReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource HoldReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string BaseUrl { get; }

    public FixtureServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUrl = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(BaseUrl);
        listener.Start();
        loop = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception) { break; }
                _ = Task.Run(() => HandleAsync(context));
            }
        });
    }

    public void Add(string path, byte[] bytes) => files[path] = bytes;
    public void Redirect(string path, string target) => redirects[path] = target;
    public void DropOnce(string path, long after) { dropPath = path; dropAfter = after; }
    public void Stall(string path, long after) { stallPath = path; stallAfter = after; }
    public void ReleaseStall() => stallRelease.TrySetResult();
    public void Hold(string path, long after) { holdPath = path; holdAfter = after; }
    public void ReleaseHold() => holdRelease.TrySetResult();

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var path = context.Request.Url!.AbsolutePath;
            var range = context.Request.Headers["Range"];
            Requests.Enqueue((path, range));
            if (redirects.TryGetValue(path, out var target))
            {
                response.StatusCode = 302;
                response.RedirectLocation = target;
                response.Close();
                return;
            }
            if (!files.TryGetValue(path, out var bytes)) { response.StatusCode = 404; response.Close(); return; }
            long start = 0, end = bytes.Length - 1;
            if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                var spec = range[6..];
                var dash = spec.IndexOf('-');
                if (dash == 0) start = Math.Max(0, bytes.Length - long.Parse(spec[1..]));
                else
                {
                    start = long.Parse(spec[..dash]);
                    if (dash < spec.Length - 1) end = Math.Min(end, long.Parse(spec[(dash + 1)..]));
                }
                if (start >= bytes.Length)
                {
                    response.StatusCode = 416;
                    response.AddHeader("Content-Range", $"bytes */{bytes.Length}");
                    response.Close();
                    return;
                }
                response.StatusCode = 206;
                response.AddHeader("Content-Range", $"bytes {start}-{end}/{bytes.Length}");
            }
            response.AddHeader("Accept-Ranges", "bytes");
            response.ContentType = "application/octet-stream";
            response.ContentLength64 = end - start + 1;
            var output = response.OutputStream;
            var download = range is null || (range.EndsWith('-') && !range.StartsWith("bytes=-", StringComparison.Ordinal));
            if (download && path == dropPath && Interlocked.Exchange(ref dropped, 1) == 0)
            {
                await output.WriteAsync(bytes.AsMemory((int)start, (int)Math.Min(dropAfter, end - start + 1)));
                await output.FlushAsync();
                await Task.Delay(300);
                response.Abort();
                return;
            }
            if (download && path == stallPath)
            {
                await output.WriteAsync(bytes.AsMemory((int)start, (int)Math.Min(stallAfter, end - start + 1)));
                await output.FlushAsync();
                StallReached.TrySetResult();
                await stallRelease.Task.WaitAsync(stop.Token);
                response.Abort();
                return;
            }
            if (download && path == holdPath)
            {
                var first = (int)Math.Min(holdAfter, end - start + 1);
                await output.WriteAsync(bytes.AsMemory((int)start, first));
                await output.FlushAsync();
                HoldReached.TrySetResult();
                await holdRelease.Task.WaitAsync(stop.Token);
                await output.WriteAsync(bytes.AsMemory((int)start + first, (int)(end - start + 1) - first));
                response.Close();
                return;
            }
            await output.WriteAsync(bytes.AsMemory((int)start, (int)(end - start + 1)));
            response.Close();
        }
        catch (Exception)
        {
            try { response.Abort(); } catch (Exception) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        stallRelease.TrySetResult();
        holdRelease.TrySetResult();
        try { listener.Stop(); listener.Close(); } catch (Exception) { }
        try { await loop; } catch (Exception) { }
        stop.Dispose();
    }
}
