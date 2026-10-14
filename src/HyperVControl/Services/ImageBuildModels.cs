using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace HyperVControl.Services;

/// <summary>
/// Offline kernel-debug settings written into a Windows image's BCD store before first boot.
/// <c>net</c> needs <c>hostIp</c> (and generates <c>key</c> when omitted); <c>serial</c> uses <c>comPort</c>.
/// </summary>
internal sealed record ImageBuildKernelDebug(bool Enabled = false, string Transport = "net", string? HostIp = null, int Port = 50000,
    string? Key = null, int ComPort = 1)
{
    public override string ToString() => $"ImageBuildKernelDebug {{ Enabled = {Enabled}, Transport = {Transport}, HostIp = {HostIp}, Port = {Port}, Key = <redacted>, ComPort = {ComPort} }}";
}

/// <summary>Raw <c>hyperv_image_build</c> arguments. Null means "not supplied", so cross-family parameters can be rejected.</summary>
internal sealed record ImageBuildRequest
{
    public string? OsFamily { get; init; }
    public string? Name { get; init; }
    public string? OutputDirectory { get; init; }
    public string? IsoPath { get; init; }
    public string? Source { get; init; }
    public string? ImagePath { get; init; }
    public string? ImageUrl { get; init; }
    public string? Sha256 { get; init; }
    public int? ImageIndex { get; init; }
    public string? Edition { get; init; }
    public int? DiskGb { get; init; }
    public string? ComputerName { get; init; }
    public string? AdminUser { get; init; }
    public string? AdminPassword { get; init; }
    public bool? EnableRdp { get; init; }
    public bool? AutoLogon { get; init; }
    public bool? PreventDeviceEncryption { get; init; }
    public ImageBuildKernelDebug? KernelDebug { get; init; }
    public string? ToolsDirectory { get; init; }
    public string? Language { get; init; }
    public string? SshUser { get; init; }
    public string? SshPublicKey { get; init; }
    public string? Password { get; init; }
    public string? Hostname { get; init; }
    public string[]? Packages { get; init; }

    public override string ToString() => $"ImageBuildRequest {{ OsFamily = {OsFamily}, Name = {Name}, OutputDirectory = {OutputDirectory} }}";
}

/// <summary>Where the build reads its OS image from.</summary>
/// <param name="Kind">iso, wim, eval-download, image or image-download.</param>
internal sealed record ImageBuildSourceSpec(string Kind, string? Path, Uri? Url, string? EvalSource, string? ExpectedSha256);

internal sealed record ImageBuildWindowsSpec(int? ImageIndex, string? Edition, string ComputerName, string AdminUser, string AdminPassword,
    bool PasswordGenerated, bool EnableRdp, bool AutoLogon, bool PreventDeviceEncryption, ImageBuildKernelDebug? KernelDebug,
    bool KeyGenerated, string? ToolsDirectory, string Language)
{
    public override string ToString() => $"ImageBuildWindowsSpec {{ ComputerName = {ComputerName}, AdminUser = {AdminUser}, AdminPassword = <redacted>, KernelDebug = {KernelDebug} }}";
}

internal sealed record ImageBuildLinuxSpec(string SshUser, IReadOnlyList<string> SshKeys, string? PasswordHash, string Hostname,
    IReadOnlyList<string> Packages, string? QemuImg)
{
    public override string ToString() => $"ImageBuildLinuxSpec {{ SshUser = {SshUser}, Keys = {SshKeys.Count}, PasswordHash = {(PasswordHash is null ? "none" : "<redacted>")}, Hostname = {Hostname} }}";
}

/// <summary>A validated build: every path is absolute and refused if it already exists; secrets are resolved.</summary>
internal sealed record ImageBuildPlan
{
    public required string OsFamily { get; init; }
    public required string Name { get; init; }
    public required string OutputDirectory { get; init; }
    public required string VhdPath { get; init; }
    public required string SidecarPath { get; init; }
    public string? SeedIsoPath { get; init; }
    public required int DiskGb { get; init; }
    public required ImageBuildSourceSpec Source { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public ImageBuildWindowsSpec? Windows { get; init; }
    public ImageBuildLinuxSpec? Linux { get; init; }
    public string CacheDirectory => Path.Combine(OutputDirectory, "cache");
}

/// <summary>VM settings the template expects; the lifecycle tools apply them when creating a VM from the image.</summary>
internal sealed record ImageBuildVmConfig(int Generation, bool SecureBoot, string SecureBootTemplate, string EnhancedSessionTransportType,
    bool Tpm, int MemoryMb, bool StaticMemory, int Processors, string CheckpointType, bool AutomaticCheckpoints);

internal sealed record ImageBuildSourceRecord(string Kind, string? Path, string? Url, string? FinalUrl, string? EvalSource,
    string? Sha256, long? Bytes, bool ChecksumVerified, string? ChecksumSource, bool Reused);

internal sealed record ImageBuildImageRecord(int Index, string Name, string? EditionId, string? Version, int? Architecture, long Size,
    IReadOnlyList<string> Languages);

/// <summary>Kernel-debug settings recorded in the sidecar. The KDNET key is returned once at build start and never stored here.</summary>
internal sealed record ImageBuildKernelDebugRecord(string Transport, string? HostIp, int? Port, int? ComPort, bool KeyStored);

/// <summary>The <c>&lt;name&gt;.image.json</c> sidecar written next to every built VHDX, for the image library to register.</summary>
internal sealed record ImageBuildSidecar(
    int SchemaVersion, string Name, string OsFamily, string Kind, string VhdPath, string? SeedIsoPath, ImageBuildVmConfig Config,
    string? AdminUser, string? SshUser, ImageBuildSourceRecord BuiltFrom, DateTimeOffset BuiltAt, ImageBuildImageRecord? Image,
    ImageBuildKernelDebugRecord? KernelDebug, string? BootConfiguration, IReadOnlyList<string> Warnings);

internal sealed record ImageBuildPartition(int Number, string GptType, long Size, string? DriveLetter);

/// <summary>The job result of a completed build. It never contains the admin password or the KDNET key.</summary>
internal sealed record ImageBuildResult(
    string OsFamily, string Name, string VhdPath, long VhdFileBytes, long? VhdVirtualBytes, string? SeedIsoPath, long? SeedIsoBytes,
    string SidecarPath, ImageBuildSidecar Sidecar, IReadOnlyList<ImageBuildPartition>? Partitions, string? BootConfiguration,
    string? BootSummary, bool? KernelDebugApplied, object? ToolsCopied, IReadOnlyList<string> Warnings, IReadOnlyList<string> NextSteps,
    double DurationSeconds);

/// <summary>Input validation for <c>hyperv_image_build</c>. Every rule runs before any host mutation.</summary>
internal static partial class ImageBuildValidation
{
    internal const string DefaultAdminUser = "labadmin";
    internal const string DefaultSshUser = "lab";
    internal const string DefaultLanguage = "en-US";
    internal const string DefaultPackage = "linux-cloud-tools-virtual";
    internal const int DefaultWindowsDiskGb = 100;
    internal const int DefaultLinuxDiskGb = 40;
    internal const int MinDiskGb = 4;
    internal const int MaxDiskGb = 65536;

    private static readonly string[] ReservedDeviceNames =
        ["CON", "PRN", "AUX", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
    private static readonly string[] ReservedWindowsAccounts = ["administrator", "guest", "defaultaccount", "wdagutilityaccount", "system"];
    internal static readonly string[] EvalSources = ["eval", "eval-ltsc", "eval-iot"];
    internal static readonly string[] LinuxImageExtensions = [".img", ".qcow2", ".vhdx", ".vhd", ".raw", ".vmdk"];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$")] private static partial Regex NamePattern();
    [GeneratedRegex("^(?!\\d+$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,13}[A-Za-z0-9])?$")] private static partial Regex ComputerNamePattern();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,19}$")] private static partial Regex WindowsUserPattern();
    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$")] private static partial Regex LinuxUserPattern();
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")] private static partial Regex HostnamePattern();
    [GeneratedRegex("^[a-z0-9][a-z0-9+.-]{1,127}$")] private static partial Regex PackagePattern();
    [GeneratedRegex("^[0-9a-fA-F]{64}$")] private static partial Regex Sha256Pattern();
    [GeneratedRegex("^[0-9a-z]{1,13}(\\.[0-9a-z]{1,13}){3}$", RegexOptions.IgnoreCase)] private static partial Regex KdKeyPattern();
    [GeneratedRegex("^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp256|ecdsa-sha2-nistp384|ecdsa-sha2-nistp521|sk-ssh-ed25519@openssh\\.com|sk-ecdsa-sha2-nistp256@openssh\\.com) [A-Za-z0-9+/]+={0,3}( [^\\x00-\\x1f\\x7f]{0,1024})?$")]
    private static partial Regex SshKeyPattern();

    internal static string OsFamily(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "windows" => "windows",
        "linux" => "linux",
        _ => throw new ArgumentException("osFamily must be windows or linux.")
    };

    internal static string Name(string? value)
    {
        if (value is null || !NamePattern().IsMatch(value) || value.EndsWith('.'))
            throw new ArgumentException("name must be 1-63 characters of letters, digits, '.', '_' or '-', start with a letter or digit, and not end with '.'; it becomes <name>.vhdx.");
        if (ReservedDeviceNames.Contains(value.Split('.')[0], StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"name '{value}' is a reserved Windows device name.");
        return value;
    }

    /// <summary>An absolute local drive path (X:\...) without '..' components, normalized.</summary>
    internal static string LocalPath(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.Split('\\', '/').Contains("..")
            || !(value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/'))
            throw new ArgumentException($"{parameter} must be an absolute local path such as D:\\Images, without '..' components.");
        var full = Path.GetFullPath(value);
        return full.Length > 3 ? full.TrimEnd('\\') : full;
    }

    internal static string ExistingFile(string value, string parameter, params string[] extensions)
    {
        var path = LocalPath(value, parameter);
        if (extensions.Length > 0 && !extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"{parameter} must be a {string.Join(", ", extensions)} file.");
        if (!File.Exists(path)) throw new ArgumentException($"{parameter} '{path}' does not exist.");
        return path;
    }

    internal static string? Sha256(string? value) => value is null ? null
        : Sha256Pattern().IsMatch(value) ? value.ToLowerInvariant()
        : throw new ArgumentException("sha256 must be 64 hexadecimal characters.");

    internal static int DiskGb(int? value, int fallback) => value switch
    {
        null => fallback,
        < MinDiskGb or > MaxDiskGb => throw new ArgumentException($"diskGb must be {MinDiskGb}..{MaxDiskGb}."),
        _ => value.Value
    };

    internal static string ComputerName(string? value, string name)
    {
        if (value is null)
        {
            var derived = new string(name.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
            derived = derived[..Math.Min(15, derived.Length)].Trim('-');
            return ComputerNamePattern().IsMatch(derived) ? derived : "*";
        }
        if (value == "*" || ComputerNamePattern().IsMatch(value)) return value;
        throw new ArgumentException("computerName must be 1-15 letters, digits or '-' (not all digits, no leading or trailing '-'), or '*' for a random name.");
    }

    internal static string WindowsUser(string? value)
    {
        var user = value ?? DefaultAdminUser;
        if (!WindowsUserPattern().IsMatch(user) || user.EndsWith('.'))
            throw new ArgumentException("adminUser must be 1-20 letters, digits, '.', '_' or '-' and start with a letter or digit.");
        if (ReservedWindowsAccounts.Contains(user, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"adminUser '{user}' is a built-in Windows account name; choose another name.");
        return user;
    }

    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>A password that XML, PowerShell Direct and RDP all accept: printable, non-blank, well-formed UTF-16, bounded length.</summary>
    internal static string Secret(string value, string parameter, int maxLength)
    {
        var valid = value.Length > 0 && value.Length <= maxLength && !string.IsNullOrWhiteSpace(value) && !value.Any(c => c < 0x20 || c == 0x7f);
        if (valid)
        {
            try { StrictUtf8.GetByteCount(value); }
            catch (System.Text.EncoderFallbackException) { valid = false; }
        }
        if (!valid)
            throw new ArgumentException($"{parameter} must be 1-{maxLength} printable characters (no control characters); a blank password is refused by RDP and Enhanced Session.");
        return value;
    }

    /// <summary>A 20-character password with upper, lower, digit and symbol, free of quotes, backslashes, '$' and backticks.</summary>
    internal static string GeneratePassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnopqrstuvwxyz", digits = "23456789", symbols = "!#%+-=?@^_";
        const string all = upper + lower + digits + symbols;
        while (true)
        {
            var candidate = RandomNumberGenerator.GetString(all, 20);
            if (candidate.IndexOfAny(upper.ToCharArray()) >= 0 && candidate.IndexOfAny(lower.ToCharArray()) >= 0
                && candidate.IndexOfAny(digits.ToCharArray()) >= 0 && candidate.IndexOfAny(symbols.ToCharArray()) >= 0)
                return candidate;
        }
    }

    internal static string Language(string? value)
    {
        var name = value ?? DefaultLanguage;
        try
        {
            var culture = CultureInfo.GetCultureInfo(name, predefinedOnly: true);
            if (culture.IsNeutralCulture || string.IsNullOrEmpty(culture.Name) || !name.Contains('-'))
                throw new CultureNotFoundException();
            return culture.Name;
        }
        catch (CultureNotFoundException)
        {
            throw new ArgumentException("language must be a specific culture name such as en-US or de-DE.");
        }
    }

    /// <summary>Same format as hyperv_kernel_configure: four base-36 components from 64 random bits each.</summary>
    internal static string NewKdKey()
    {
        static string Base36(ulong value)
        {
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
            var text = "";
            do { text = digits[(int)(value % 36)] + text; value /= 36; } while (value != 0);
            return text;
        }
        return string.Join('.', Enumerable.Range(0, 4).Select(_ => Base36(BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)))));
    }

    internal static (ImageBuildKernelDebug? Settings, bool KeyGenerated) KernelDebug(ImageBuildKernelDebug? value)
    {
        if (value is null || !value.Enabled) return (null, false);
        switch (value.Transport?.Trim().ToLowerInvariant())
        {
            case "net":
                if (!IPAddress.TryParse(value.HostIp, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                    throw new ArgumentException("kernelDebug.hostIp must be the host's IPv4 address that the guest can reach.");
                if (value.Port is < 1024 or > 65535)
                    throw new ArgumentException("kernelDebug.port must be 1024..65535; use a unique port per VM (typically 50000..50039).");
                var generated = value.Key is null;
                var key = (value.Key ?? NewKdKey()).ToLowerInvariant();
                if (!KdKeyPattern().IsMatch(key)) throw new ArgumentException("kernelDebug.key must be four dot-separated base-36 components, such as 1a.2b.3c.4d.");
                return (new ImageBuildKernelDebug(true, "net", address.ToString(), value.Port, key, value.ComPort), generated);
            case "serial":
                if (value.ComPort is not (1 or 2)) throw new ArgumentException("kernelDebug.comPort must be 1 or 2.");
                if (value.HostIp is not null || value.Key is not null) throw new ArgumentException("kernelDebug.hostIp and key apply to transport=net only.");
                return (new ImageBuildKernelDebug(true, "serial", null, value.Port, null, value.ComPort), false);
            default:
                throw new ArgumentException("kernelDebug.transport must be net or serial.");
        }
    }

    internal static string LinuxUser(string? value)
    {
        var user = value ?? DefaultSshUser;
        if (!LinuxUserPattern().IsMatch(user) || user == "root")
            throw new ArgumentException("sshUser must be a lowercase Linux user name (letters, digits, '_' or '-', starting with a letter or '_'), not root.");
        return user;
    }

    internal static IReadOnlyList<string> SshKeys(string? value)
    {
        if (value is null) return [];
        var keys = value.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        if (keys.Count is 0 or > 16) throw new ArgumentException("sshPublicKey must contain 1-16 OpenSSH public keys, one per line.");
        foreach (var key in keys)
            if (key.Length > 16384 || !SshKeyPattern().IsMatch(key))
                throw new ArgumentException("sshPublicKey must be OpenSSH public key lines such as 'ssh-ed25519 AAAA... comment'.");
        return keys;
    }

    internal static string Hostname(string? value, string name)
    {
        if (value is null)
        {
            var derived = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
            derived = derived[..Math.Min(63, derived.Length)].Trim('-');
            return HostnamePattern().IsMatch(derived) ? derived : "ubuntu";
        }
        var lower = value.ToLowerInvariant();
        return HostnamePattern().IsMatch(lower) ? lower
            : throw new ArgumentException("hostname must be one DNS label: 1-63 letters, digits or '-', without leading or trailing '-'.");
    }

    internal static IReadOnlyList<string> Packages(string[]? value)
    {
        if (value is null) return [DefaultPackage];
        if (value.Length > 64) throw new ArgumentException("packages accepts at most 64 package names.");
        foreach (var package in value)
            if (package is null || !PackagePattern().IsMatch(package))
                throw new ArgumentException($"Invalid package name '{package}'. Use Debian package names such as linux-cloud-tools-virtual.");
        return value.Distinct(StringComparer.Ordinal).ToList();
    }

    internal static Uri ImageUrl(string value, bool hasSha256)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(url.UserInfo))
            throw new ArgumentException("imageUrl must be an absolute https:// URL without credentials.");
        if (url.Scheme == "http" && !hasSha256)
            throw new ArgumentException("An http:// imageUrl needs sha256; prefer https://.");
        return url;
    }

    internal static string ToolsDirectory(string value, string outputDirectory)
    {
        var path = LocalPath(value, "toolsDirectory");
        if (!Directory.Exists(path)) throw new ArgumentException($"toolsDirectory '{path}' does not exist.");
        if (Path.GetPathRoot(path)!.TrimEnd('\\').Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("toolsDirectory must be a folder, not a drive root.");
        if (IsSameOrUnder(outputDirectory, path))
            throw new ArgumentException("toolsDirectory must not contain the output directory.");
        return path;
    }

    internal static bool IsSameOrUnder(string path, string directory)
    {
        var a = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        var b = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rejects parameters that belong to the other OS family, so a misplaced argument is never silently ignored.</summary>
    internal static void RejectForeign(string family, ImageBuildRequest r)
    {
        (string Name, bool Given)[] windowsOnly = [("isoPath", r.IsoPath is not null), ("source", r.Source is not null), ("imageIndex", r.ImageIndex is not null),
            ("edition", r.Edition is not null), ("computerName", r.ComputerName is not null), ("adminUser", r.AdminUser is not null),
            ("adminPassword", r.AdminPassword is not null), ("enableRdp", r.EnableRdp is not null), ("autoLogon", r.AutoLogon is not null),
            ("preventDeviceEncryption", r.PreventDeviceEncryption is not null), ("kernelDebug", r.KernelDebug is not null),
            ("toolsDirectory", r.ToolsDirectory is not null), ("language", r.Language is not null)];
        (string Name, bool Given)[] linuxOnly = [("imageUrl", r.ImageUrl is not null), ("sshUser", r.SshUser is not null),
            ("sshPublicKey", r.SshPublicKey is not null), ("password", r.Password is not null), ("hostname", r.Hostname is not null),
            ("packages", r.Packages is not null)];
        var foreign = (family == "windows" ? linuxOnly : windowsOnly).Where(p => p.Given).Select(p => p.Name).ToList();
        if (foreign.Count > 0)
            throw new ArgumentException($"{string.Join(", ", foreign)} {(foreign.Count == 1 ? "applies" : "apply")} to osFamily={(family == "windows" ? "linux" : "windows")} only.");
    }
}
