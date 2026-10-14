using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HyperVControl.Services;

/// <summary>A name/value pair from a feed entry's <c>details</c> list.</summary>
internal sealed record CatalogDetail(string Name, string Value);

/// <summary>
/// One image from a Quick Create gallery feed (Microsoft's or a custom catalog), normalized leniently:
/// <c>secureBoot</c> may be a string or a bool, <see cref="Sha256"/> keeps only the hex after the hash
/// prefix (always SHA-256, as VMCreate treats it), and warnings, age and OS family are derived at parse time.
/// </summary>
internal sealed record CatalogImage(
    string Catalog, string Name, string? Publisher, string? Version, DateTimeOffset? LastUpdated, string? Locale,
    string Description, string DiskUri, string? Hash, string? Sha256, string? ArchiveRelativePath,
    bool? SecureBoot, string? EnhancedSessionTransportType, string? MinHostVersion, string? MaxHostVersion,
    IReadOnlyList<CatalogDetail> Details, string OsFamily, IReadOnlyList<string> Warnings,
    bool Stale, bool EndOfLife, bool Fetchable)
{
    /// <summary>The disk type implied by the URI path before redirects: ".zip", ".vhdx", ".iso", another extension, or "".</summary>
    public string DiskExtension => ImageCatalogFeed.ExtensionOf(DiskUri);
}

/// <summary>A parsed feed: compatible images, the number hidden by the host version filter, and skipped entries.</summary>
internal sealed record CatalogFeed(string Catalog, IReadOnlyList<CatalogImage> Images, int Hidden, IReadOnlyList<string> Skipped);

/// <summary>
/// Parses Quick Create gallery feeds the way VMCreate accepts them: UTF-16 or UTF-8 with or without a BOM,
/// trailing commas, comments, case-insensitive property names, and string-or-bool settings. Listing never
/// touches disk URLs; everything here is derived from the feed text alone.
/// </summary>
internal static class ImageCatalogFeed
{
    public const string MicrosoftName = "microsoft";
    public const string MicrosoftLocation = "https://go.microsoft.com/fwlink/?linkid=851584";
    public static readonly string[] DiskExtensions = [".zip", ".vhdx", ".iso"];
    private const int MaxDescription = 600;

    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 };
    private static readonly Regex WinDev = new(@"WinDev\d{4}Eval", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Ubuntu = new(@"\bUbuntu\s+(?<release>\d{2}\.\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WindowsWord = new(@"\bwindows\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LinuxWord = new(@"\b(ubuntu|debian|fedora|centos|red hat|rhel|suse|linux|almalinux|rocky|kali|mint|canonical|alpine)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // End of standard support for Ubuntu LTS releases (https://ubuntu.com/about/release-cycle), by month.
    private static readonly Dictionary<string, (DateTimeOffset After, string Month)> UbuntuSupportEnd = new()
    {
        ["16.04"] = (new(2021, 5, 1, 0, 0, 0, TimeSpan.Zero), "April 2021"),
        ["18.04"] = (new(2023, 6, 1, 0, 0, 0, TimeSpan.Zero), "May 2023"),
        ["20.04"] = (new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero), "May 2025"),
        ["22.04"] = (new(2027, 6, 1, 0, 0, 0, TimeSpan.Zero), "May 2027"),
    };

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];
    private static ReadOnlySpan<byte> Utf32LeBom => [0xFF, 0xFE, 0x00, 0x00];
    private static ReadOnlySpan<byte> Utf32BeBom => [0x00, 0x00, 0xFE, 0xFF];
    private static ReadOnlySpan<byte> Utf16LeBom => [0xFF, 0xFE];
    private static ReadOnlySpan<byte> Utf16BeBom => [0xFE, 0xFF];

    /// <summary>Decodes feed bytes by BOM (UTF-8, UTF-16 LE/BE, UTF-32), detecting BOM-less UTF-16; UTF-8 otherwise.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Utf8Bom)) return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.StartsWith(Utf32LeBom)) return new UTF32Encoding(false, false).GetString(bytes[4..]);
        if (bytes.StartsWith(Utf32BeBom)) return new UTF32Encoding(true, false).GetString(bytes[4..]);
        if (bytes.StartsWith(Utf16LeBom)) return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.StartsWith(Utf16BeBom)) return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        // BOM-less UTF-16 puts a zero byte beside every ASCII character.
        if (bytes.Length >= 4 && bytes[0] != 0 && bytes[1] == 0 && bytes[3] == 0) return Encoding.Unicode.GetString(bytes);
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] != 0 && bytes[2] == 0) return Encoding.BigEndianUnicode.GetString(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>An http(s) URL, a file: URI, or an absolute local or UNC path (which become file: URIs).</summary>
    public static Uri ParseLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location)) throw new ArgumentException("A location is required.");
        if (Uri.TryCreate(location.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || (uri.IsFile && Path.IsPathFullyQualified(uri.LocalPath))))
            return uri;
        throw new ArgumentException($"'{location}' is not an http(s) URL, a file: URI, or an absolute local or UNC path.");
    }

    public static string ExtensionOf(string uriOrPath)
    {
        try
        {
            var path = Uri.TryCreate(uriOrPath, UriKind.Absolute, out var uri) ? (uri.IsFile ? uri.LocalPath : uri.AbsolutePath) : uriOrPath;
            return Path.GetExtension(path).ToLowerInvariant();
        }
        catch (ArgumentException) { return ""; }
    }

    /// <summary>The hex digest VMCreate compares: the text after the first ':' (any prefix, always SHA-256), or a bare digest.</summary>
    public static string? Sha256Of(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return null;
        var colon = hash.IndexOf(':');
        var hex = (colon >= 0 ? hash[(colon + 1)..] : hash).Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    /// <summary>Parses one feed. Malformed JSON throws; malformed entries are skipped and named in <see cref="CatalogFeed.Skipped"/>.</summary>
    public static CatalogFeed Parse(string catalog, string json, Version host, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json, Lenient);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Child(root, "images") is not { ValueKind: JsonValueKind.Array } list)
            throw new InvalidDataException("A gallery feed is a JSON object with an \"images\" array.");
        var images = new List<CatalogImage>();
        var skipped = new List<string>();
        var hidden = 0;
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.Object) { skipped.Add($"entry {index}: not a JSON object"); continue; }
            var name = Text(Child(item, "name"))?.Trim();
            var disk = Child(item, "disk");
            var uri = disk is { ValueKind: JsonValueKind.Object } diskObject ? Text(Child(diskObject, "uri"))?.Trim() : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(uri)) { skipped.Add($"entry {index}: missing name or disk.uri"); continue; }
            var min = Text(Child(item, "minHostVersion"))?.Trim();
            var max = Text(Child(item, "maxHostVersion"))?.Trim();
            if (!HostAllows(host, min, max)) { hidden++; continue; }
            images.Add(Build(catalog, name, item, disk!.Value, uri, min, max, now));
        }
        return new(catalog, images, hidden, skipped);
    }

    private static CatalogImage Build(string catalog, string name, JsonElement item, JsonElement disk, string uri, string? min, string? max, DateTimeOffset now)
    {
        var publisher = Text(Child(item, "publisher"))?.Trim();
        var version = Text(Child(item, "version"))?.Trim();
        var updatedText = Text(Child(item, "lastUpdated"))?.Trim();
        DateTimeOffset? updated = DateTimeOffset.TryParse(updatedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        var hash = Text(Child(disk, "hash"))?.Trim();
        var archive = Text(Child(disk, "archiveRelativePath"))?.Trim();
        if (string.IsNullOrEmpty(archive)) archive = null;
        var config = Child(item, "config");
        bool? secureBoot = config is { ValueKind: JsonValueKind.Object } c1 ? Bool(Child(c1, "secureBoot")) : null;
        var transportText = config is { ValueKind: JsonValueKind.Object } c2 ? Text(Child(c2, "enhancedSessionTransportType"))?.Trim() : null;
        string? transport = transportText?.ToLowerInvariant() switch { "hvsocket" => "HvSocket", "vmbus" => "VMBus", _ => null };
        var details = new List<CatalogDetail>();
        if (Child(item, "details") is { ValueKind: JsonValueKind.Array } detailList)
            foreach (var detail in detailList.EnumerateArray())
                if (detail.ValueKind == JsonValueKind.Object && Text(Child(detail, "value")) is { } value)
                    details.Add(new(Text(Child(detail, "name")) ?? "", value));
        var description = Describe(Child(item, "description"));
        var family = InferOsFamily(name, publisher, details);
        var sha256 = Sha256Of(hash);

        var warnings = new List<string>();
        // A Windows evaluation is informational, never a reason to skip the image: the feed publishes no end date, and
        // Hyper-V Quick Create still downloads and runs Microsoft's Windows 11 dev environment (verified by the user on 2026-10-14).
        if (WinDev.IsMatch(name + " " + uri + " " + archive) || (family == "windows" && IsEvaluation(name, description, details)))
        {
            var edition = details.FirstOrDefault(d => string.Equals(d.Name.Trim(), "Edition", StringComparison.OrdinalIgnoreCase))?.Value.Trim() is { Length: > 0 } named
                ? named : "Windows Enterprise";
            if (!edition.Contains("Evaluation", StringComparison.OrdinalIgnoreCase)) edition += " Evaluation";
            warnings.Add($"Evaluation edition: {edition}" + (updated is { } evaluated ? $", last updated {evaluated:yyyy-MM-dd}" : "") + ". "
                + "Hyper-V Quick Create still downloads and runs it. The evaluation license is time-limited (run `slmgr /xpr` inside the guest for the end date; "
                + "Windows evaluations run about 90 days), so take a checkpoint after the first boot and recreate the VM from the image when it expires.");
        }

        var stale = updated is { } age && age < now.AddYears(-3);
        if (stale) warnings.Add($"Stale image: last updated {updated:yyyy-MM-dd}, more than 3 years ago; expect outdated software and a long first update.");
        else if (updated is null) warnings.Add("No lastUpdated date: the image's age is unknown.");

        var endOfLife = false;
        var ubuntu = Ubuntu.Match(name + " " + version);
        if (ubuntu.Success && UbuntuSupportEnd.TryGetValue(ubuntu.Groups["release"].Value, out var support) && now >= support.After)
        {
            endOfLife = true;
            warnings.Add($"Ubuntu {ubuntu.Groups["release"].Value} left standard support in {support.Month}; it receives no security updates without Ubuntu Pro.");
        }

        var fetchable = true;
        if (hash is null) { fetchable = false; warnings.Add("No hash: the entry has no SHA-256, so its download cannot be verified and fetch refuses it."); }
        else if (sha256 is null) { fetchable = false; warnings.Add($"Invalid hash '{hash}': expected '<prefix>:<64 hex digits>' (always SHA-256); fetch refuses it."); }
        var extension = ExtensionOf(uri);
        if (extension.Length > 0 && !DiskExtensions.Contains(extension))
        {
            fetchable = false;
            warnings.Add($"Unsupported disk type '{extension}': Quick Create images are a .zip containing a .vhdx, a .vhdx or an .iso.");
        }
        else if (extension == ".zip" && archive is null)
        {
            fetchable = false;
            warnings.Add("The disk is a .zip without archiveRelativePath, so there is no entry to extract; fetch refuses it.");
        }
        if (archive is not null && Path.GetExtension(archive).ToLowerInvariant() is not (".vhdx" or ".iso"))
        {
            fetchable = false;
            warnings.Add($"The archive entry '{archive}' is not a .vhdx or .iso; fetch refuses it.");
        }
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri) && parsedUri.Scheme == Uri.UriSchemeHttp)
            warnings.Add("The disk is served over plain HTTP; only its SHA-256 protects the download.");

        return new(catalog, name, publisher, version, updated, Text(Child(item, "locale")), description, uri, hash, sha256, archive,
            secureBoot, transport, min, max, details, family, warnings, stale, endOfLife, fetchable);
    }

    /// <summary>VMCreate hides images whose minHostVersion/maxHostVersion exclude the host; unparseable bounds are ignored.</summary>
    public static bool HostAllows(Version host, string? min, string? max) =>
        !(Version.TryParse(min, out var low) && host < low) && !(Version.TryParse(max, out var high) && host > high);

    public static string InferOsFamily(string name, string? publisher, IEnumerable<CatalogDetail> details)
    {
        var title = name + " " + publisher;
        if (WindowsWord.IsMatch(title)) return "windows";
        var text = title + " " + string.Join(' ', details.Select(d => d.Value));
        if (LinuxWord.IsMatch(text)) return "linux";
        return WindowsWord.IsMatch(text) ? "windows" : "other";
    }

    private static bool IsEvaluation(string name, string description, IReadOnlyList<CatalogDetail> details) =>
        name.Contains("eval", StringComparison.OrdinalIgnoreCase)
        || description.Contains("evaluation", StringComparison.OrdinalIgnoreCase)
        || details.Any(d => d.Value.Contains("evaluation", StringComparison.OrdinalIgnoreCase));

    private static string Describe(JsonElement? element)
    {
        var text = element switch
        {
            { ValueKind: JsonValueKind.Array } array => string.Concat(array.EnumerateArray().Select(e => Text(e) ?? "")),
            { } single => Text(single) ?? "",
            null => ""
        };
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= MaxDescription ? text : text[..(MaxDescription - 1)] + "…";
    }

    private static JsonElement? Child(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static string? Text(JsonElement? element) => element?.ValueKind switch
    {
        JsonValueKind.String => element.Value.GetString(),
        JsonValueKind.Number => element.Value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    private static bool? Bool(JsonElement? element) => element?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.Value.GetString()?.Trim().ToLowerInvariant() switch { "true" => true, "false" => false, _ => null },
        _ => null
    };
}
