using System.Security.Cryptography;
using System.Text;

namespace HyperVControl.Services;

/// <summary>
/// NoCloud seed content for Ubuntu cloud images: <c>user-data</c> (cloud-config) and <c>meta-data</c>, both with LF line
/// endings and no byte order mark, as cloud-init expects. Values are emitted as double-quoted YAML scalars; inputs are
/// validated beforehand so no control characters can reach the document.
/// </summary>
internal static class ImageBuildCloudInit
{
    internal const string VolumeLabel = "CIDATA";
    internal static readonly Encoding FileEncoding = new UTF8Encoding(false);

    public static string UserData(string user, IReadOnlyList<string> sshKeys, string? passwordHash, string hostname, IReadOnlyList<string> packages)
    {
        var y = new StringBuilder();
        y.Append("#cloud-config\n");
        y.Append("hostname: ").Append(Quote(hostname)).Append('\n');
        y.Append("users:\n");
        y.Append("  - name: ").Append(Quote(user)).Append('\n');
        y.Append("    groups: [sudo]\n");
        y.Append("    shell: /bin/bash\n");
        y.Append("    sudo: \"ALL=(ALL) NOPASSWD:ALL\"\n");
        y.Append("    lock_passwd: ").Append(passwordHash is null ? "true" : "false").Append('\n');
        if (passwordHash is not null) y.Append("    passwd: ").Append(Quote(passwordHash)).Append('\n');
        if (sshKeys.Count > 0)
        {
            y.Append("    ssh_authorized_keys:\n");
            foreach (var key in sshKeys) y.Append("      - ").Append(Quote(key)).Append('\n');
        }
        // Password SSH only when no key was supplied; a key-only image keeps password authentication off.
        y.Append("ssh_pwauth: ").Append(passwordHash is not null && sshKeys.Count == 0 ? "true" : "false").Append('\n');
        if (packages.Count > 0)
        {
            y.Append("package_update: true\n");
            y.Append("packages:\n");
            foreach (var package in packages) y.Append("  - ").Append(Quote(package)).Append('\n');
        }
        return y.ToString();
    }

    public static string MetaData(string instanceId, string hostname) =>
        "instance-id: " + Quote(instanceId) + "\nlocal-hostname: " + Quote(hostname) + "\n";

    /// <summary>Writes <c>user-data</c> and <c>meta-data</c> into an empty staging directory for the ISO builder.</summary>
    public static void WriteSeed(string directory, string userData, string metaData)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "user-data"), userData, FileEncoding);
        File.WriteAllText(Path.Combine(directory, "meta-data"), metaData, FileEncoding);
    }

    internal static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private const string CryptAlphabet = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// SHA-512-crypt (<c>$6$</c>, 5000 rounds), the format cloud-init's <c>passwd</c> and /etc/shadow expect, so the seed ISO
    /// never carries a plain-text password. Implements Ulrich Drepper's published "Unix crypt using SHA-256 and SHA-512".
    /// </summary>
    public static string Sha512Crypt(string password, string? salt = null)
    {
        salt ??= RandomNumberGenerator.GetString(CryptAlphabet, 16);
        if (salt.Length > 16) salt = salt[..16];
        var p = Encoding.UTF8.GetBytes(password);
        var s = Encoding.ASCII.GetBytes(salt);

        var b = SHA512.HashData(p.Concat(s).Concat(p).ToArray());
        using var a = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        a.AppendData(p);
        a.AppendData(s);
        for (var n = p.Length; n > 0; n -= 64) a.AppendData(b, 0, Math.Min(64, n));
        for (var n = p.Length; n > 0; n >>= 1) a.AppendData((n & 1) != 0 ? b : p);
        var c = a.GetHashAndReset();

        using var dpHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        for (var i = 0; i < p.Length; i++) dpHash.AppendData(p);
        var pBytes = Repeat(dpHash.GetHashAndReset(), p.Length);

        using var dsHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        for (var i = 0; i < 16 + c[0]; i++) dsHash.AppendData(s);
        var sBytes = Repeat(dsHash.GetHashAndReset(), s.Length);

        using var round = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        for (var i = 0; i < 5000; i++)
        {
            round.AppendData((i & 1) != 0 ? pBytes : c);
            if (i % 3 != 0) round.AppendData(sBytes);
            if (i % 7 != 0) round.AppendData(pBytes);
            round.AppendData((i & 1) != 0 ? c : pBytes);
            c = round.GetHashAndReset();
        }

        int[][] order =
        [
            [0, 21, 42], [22, 43, 1], [44, 2, 23], [3, 24, 45], [25, 46, 4], [47, 5, 26], [6, 27, 48], [28, 49, 7], [50, 8, 29], [9, 30, 51],
            [31, 52, 10], [53, 11, 32], [12, 33, 54], [34, 55, 13], [56, 14, 35], [15, 36, 57], [37, 58, 16], [59, 17, 38], [18, 39, 60],
            [40, 61, 19], [62, 20, 41]
        ];
        var output = new StringBuilder("$6$").Append(salt).Append('$');
        foreach (var group in order) Base64(output, c[group[0]], c[group[1]], c[group[2]], 4);
        Base64(output, 0, 0, c[63], 2);
        return output.ToString();
    }

    private static byte[] Repeat(byte[] digest, int length)
    {
        var result = new byte[length];
        for (var i = 0; i < length; i++) result[i] = digest[i % digest.Length];
        return result;
    }

    private static void Base64(StringBuilder output, byte b2, byte b1, byte b0, int count)
    {
        var w = (b2 << 16) | (b1 << 8) | b0;
        for (var i = 0; i < count; i++) { output.Append(CryptAlphabet[w & 0x3f]); w >>= 6; }
    }
}
