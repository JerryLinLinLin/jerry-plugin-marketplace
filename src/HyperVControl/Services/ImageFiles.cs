using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HyperVControl.Services;

/// <summary>One entry of a zip central directory, with Zip64 sizes already applied.</summary>
internal sealed record ZipEntryInfo(string FullName, long CompressedSize, long UncompressedSize, int Method);

/// <summary>
/// File-level image work, all in C#: the magic bytes VMCreate checks, streaming SHA-256, free-space
/// preflight, safe file names, and cancellation-aware copies and zip extraction.
/// </summary>
internal static class ImageFiles
{
    /// <summary>Headroom kept free on the target volume beyond what a download or extraction needs.</summary>
    public const long SpaceMargin = 1L << 30;
    internal const int BufferSize = 1 << 20;

    private static ReadOnlySpan<byte> ZipMagic => [0x50, 0x4B, 0x03, 0x04];
    private static ReadOnlySpan<byte> VhdxMagic => "vhdxfile"u8;
    private static ReadOnlySpan<byte> IsoMagic => "CD001"u8;
    private static readonly long[] IsoOffsets = [0x8001, 0x8801, 0x9001];
    private static readonly Regex ReservedName = new(@"^(con|prn|aux|nul|com\d|lpt\d)(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when the file carries the signature VMCreate expects for its extension
    /// (zip <c>PK\x03\x04</c>, vhdx <c>vhdxfile</c>, iso <c>CD001</c> at 0x8001, 0x8801 or 0x9001).</summary>
    public static bool HasMagic(string path, string extension)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
        switch (extension.ToLowerInvariant())
        {
            case ".zip": return Matches(stream, 0, ZipMagic);
            case ".vhdx": return Matches(stream, 0, VhdxMagic);
            case ".iso":
                foreach (var offset in IsoOffsets)
                    if (Matches(stream, offset, IsoMagic)) return true;
                return false;
            default: return false;
        }
    }

    private static bool Matches(FileStream stream, long offset, ReadOnlySpan<byte> magic)
    {
        if (stream.Length < offset + magic.Length) return false;
        Span<byte> buffer = stackalloc byte[magic.Length];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer.SequenceEqual(magic);
    }

    /// <summary>A conservative file name: ASCII letters, digits, '.', '_' and '-' only, at most 80 characters.</summary>
    public static string SafeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Normalize(NormalizationForm.FormKC))
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '-');
        var safe = Regex.Replace(builder.ToString(), "-{2,}", "-").Trim('-', '.', '_');
        if (safe.Length > 80) safe = safe[..80].TrimEnd('-', '.', '_');
        if (safe.Length == 0) safe = "image";
        return ReservedName.IsMatch(safe) ? "image-" + safe : safe;
    }

    /// <summary>A directory name that keeps the VM name readable: only characters Windows rejects are replaced.</summary>
    public static string SafeDirectoryName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (safe.Length == 0) safe = "vm";
        return ReservedName.IsMatch(safe) ? "vm-" + safe : safe;
    }

    public static bool SameEntry(string entryName, string relativePath) =>
        string.Equals(entryName.Replace('\\', '/').TrimStart('/'), relativePath.Replace('\\', '/').TrimStart('/'), StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    /// <summary>Bytes available to this user on the volume holding <paramref name="directory"/> (mount points and UNC paths included).</summary>
    public static long FreeBytes(string directory)
    {
        var path = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        if (!GetDiskFreeSpaceExW(path, out var available, out _, out _))
            throw new IOException($"Cannot read the free space of {directory} (Win32 error {Marshal.GetLastPInvokeError()}).");
        return (long)Math.Min(available, long.MaxValue);
    }

    /// <summary>Fails with <c>InsufficientDiskSpace</c> unless <paramref name="needed"/> bytes plus a 1 GB margin are free.</summary>
    public static void EnsureSpace(string directory, long needed, string purpose)
    {
        var free = FreeBytes(directory);
        var required = Math.Max(0, needed) + SpaceMargin;
        if (free < required)
            throw new HyperVOperationException($"Not enough free space for {purpose} in {directory}: {Gb(required)} needed (including a 1 GB margin), {Gb(free)} free. "
                + $"Free space or set {ImageCatalogService.RootVariable} to a larger volume.", "InsufficientDiskSpace");
    }

    public static string Gb(long bytes) => (bytes / 1e9).ToString("0.0#", CultureInfo.InvariantCulture) + " GB";

    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>Feeds up to <paramref name="limit"/> bytes of <paramref name="stream"/> into <paramref name="hash"/>; returns the bytes read.</summary>
    public static async Task<long> HashStreamAsync(Stream stream, IncrementalHash hash, long limit, Action<long>? progress, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long done = 0;
            while (done < limit)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - done)), ct);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                done += read;
                progress?.Invoke(done);
            }
            return done;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static async Task<string> Sha256Async(string path, Action<long>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = OpenRead(path);
        await HashStreamAsync(stream, hash, long.MaxValue, progress, ct);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Copies a file to a new path with cancellation and progress; the target is pre-sized and never overwritten.</summary>
    public static async Task CopyAsync(string source, string target, Action<long, long>? progress, CancellationToken ct)
    {
        await using var input = OpenRead(source);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = input.Length;
        output.SetLength(length);
        var done = await PumpAsync(input, output, length, progress, ct);
        if (done != length) throw new IOException($"Copied {done} of {length} bytes from {source}.");
    }

    /// <summary>
    /// Streams one entry, matched case-insensitively against <see cref="ZipArchiveEntry.FullName"/> as VMCreate does,
    /// to <paramref name="target"/>. The archive reader handles Zip64; free space is checked against the entry size first.
    /// </summary>
    public static async Task<long> ExtractAsync(string archivePath, string relativePath, string target, Action<long, long>? progress, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.FirstOrDefault(e => SameEntry(e.FullName, relativePath))
            ?? throw new HyperVOperationException($"The archive has no entry '{relativePath}'. Entries: {string.Join(", ", archive.Entries.Take(20).Select(e => e.FullName))}.", "UnsupportedImage");
        EnsureSpace(Path.GetDirectoryName(target)!, entry.Length, "extracting " + entry.FullName);
        await using var input = entry.Open();
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        output.SetLength(entry.Length);
        var done = await PumpAsync(input, output, entry.Length, progress, ct);
        if (done != entry.Length) throw new InvalidDataException($"The entry '{entry.FullName}' produced {done} of {entry.Length} bytes.");
        return done;
    }

    private static async Task<long> PumpAsync(Stream input, Stream output, long total, Action<long, long>? progress, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress?.Invoke(done, total);
            }
            await output.FlushAsync(ct);
            return done;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static void TryDelete(string? path)
    {
        if (path is null) return;
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Reads a zip central directory from the archive's tail, so a remote archive's entry sizes are known from a
/// 64 KB range request before downloading it. Handles Zip64 (EOCD locator, Zip64 EOCD record and the
/// extended-information extra field); further ranges are read through a caller-supplied reader.
/// </summary>
internal static class ImageZip
{
    private const uint EndOfCentralDirectory = 0x06054b50, Zip64Locator = 0x07064b50, Zip64EndOfCentralDirectory = 0x06064b50, CentralHeader = 0x02014b50;
    private const int MaxCentralDirectory = 64 << 20;

    public delegate Task<byte[]> RangeReader(long offset, int count, CancellationToken ct);

    /// <param name="length">The archive length.</param>
    /// <param name="tail">The last <c>tail.Length</c> bytes of the archive.</param>
    /// <param name="read">Reads bytes outside the tail (another range request, or a file read).</param>
    public static async Task<IReadOnlyList<ZipEntryInfo>> ReadCentralDirectoryAsync(long length, byte[] tail, RangeReader read, CancellationToken ct)
    {
        var tailStart = length - tail.Length;
        var end = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EndOfCentralDirectory) { end = i; break; }
        if (end < 0) throw new InvalidDataException("No zip end-of-central-directory record in the archive's last " + tail.Length + " bytes.");
        ulong entries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
        ulong size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12));
        ulong offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
        if (entries == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF)
        {
            var locator = await SliceAsync(tailStart + end - 20, 20);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64Locator) throw new InvalidDataException("Zip64 end-of-central-directory locator missing.");
            var recordOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            var record = await SliceAsync(recordOffset, 56);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Zip64EndOfCentralDirectory) throw new InvalidDataException("Zip64 end-of-central-directory record missing.");
            entries = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32));
            size = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40));
            offset = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48));
        }
        if (size > MaxCentralDirectory || offset + size > (ulong)length) throw new InvalidDataException("The zip central directory is out of range.");
        var directory = await SliceAsync((long)offset, (int)size);
        var result = new List<ZipEntryInfo>();
        var position = 0;
        for (ulong n = 0; n < entries && position + 46 <= directory.Length; n++)
        {
            var header = directory.AsSpan(position);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralHeader) throw new InvalidDataException("Corrupt zip central directory header.");
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            long uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            if (46 + nameLength + extraLength > header.Length) throw new InvalidDataException("Truncated zip central directory entry.");
            var name = ((flags & 0x0800) != 0 ? Encoding.UTF8 : Encoding.Latin1).GetString(header.Slice(46, nameLength));
            var extra = header.Slice(46 + nameLength, extraLength);
            while (extra.Length >= 4)
            {
                var id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
                var dataLength = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
                if (4 + dataLength > extra.Length) break;
                if (id == 0x0001)
                {
                    // Zip64 extended information: only the fields whose 32-bit value is saturated, in this order.
                    var data = extra.Slice(4, dataLength);
                    if (uncompressed == 0xFFFFFFFF && data.Length >= 8) { uncompressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(data); data = data[8..]; }
                    if (compressed == 0xFFFFFFFF && data.Length >= 8) compressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(data);
                }
                extra = extra[(4 + dataLength)..];
            }
            result.Add(new(name, compressed, uncompressed, method));
            position += 46 + nameLength + extraLength + commentLength;
        }
        return result;

        Task<byte[]> SliceAsync(long start, int count)
        {
            if (start < 0 || start + count > length) throw new InvalidDataException("A zip record points outside the archive.");
            return start >= tailStart ? Task.FromResult(tail[(int)(start - tailStart)..(int)(start - tailStart + count)]) : read(start, count, ct);
        }
    }

    /// <summary>Reads the central directory of a local archive through the same parser (tests and diagnostics).</summary>
    public static async Task<IReadOnlyList<ZipEntryInfo>> ReadCentralDirectoryAsync(string path, CancellationToken ct)
    {
        await using var stream = ImageFiles.OpenRead(path);
        var tail = new byte[(int)Math.Min(stream.Length, ImageDownloader.TailBytes)];
        stream.Position = stream.Length - tail.Length;
        await stream.ReadExactlyAsync(tail, ct);
        return await ReadCentralDirectoryAsync(stream.Length, tail, async (offset, count, token) =>
        {
            var bytes = new byte[count];
            stream.Position = offset;
            await stream.ReadExactlyAsync(bytes, token);
            return bytes;
        }, ct);
    }
}
