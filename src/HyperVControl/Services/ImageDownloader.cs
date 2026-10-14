using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace HyperVControl.Services;

/// <summary>Where a catalog disk URI resolved to: the final URL after redirects, its type and size, and,
/// for a zip whose server honors range requests, the central directory read from the archive's tail.</summary>
internal sealed record ImageProbe(Uri FinalUri, string Extension, long? Length, bool AcceptsRanges, IReadOnlyList<ZipEntryInfo>? ZipEntries, string? Note);

/// <summary>One finished download: its length, the bytes received now, and the bytes reused from an earlier partial file.</summary>
internal sealed record ImageDownload(long Length, long Downloaded, long ResumedFrom, string Sha256);

/// <summary>
/// The HTTP side of a fetch. One suffix-range GET resolves redirects, the size and (for zips) the central
/// directory without downloading the payload. The download resumes an existing partial file with a Range
/// request, hashes while it writes, and reports a dropped or stalled connection as <c>DownloadInterrupted</c>,
/// keeping the partial file so the next fetch resumes it. A hash mismatch deletes the file.
/// </summary>
internal sealed class ImageDownloader(HttpClient http, TimeSpan stallTimeout)
{
    internal const int TailBytes = 64 * 1024;
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(60);

    public async Task<ImageProbe> ProbeAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(null, TailBytes);
        using var response = await SendAsync(request, ct);
        var final = response.RequestMessage?.RequestUri ?? uri;
        var extension = ImageCatalogFeed.ExtensionOf(final.AbsoluteUri);
        if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange is { Length: long length })
        {
            var tail = await ReadBodyAsync(response, TailBytes, ct);
            if (extension != ".zip") return new(final, extension, length, true, null, null);
            try
            {
                var entries = await ImageZip.ReadCentralDirectoryAsync(length, tail, (offset, count, token) => RangeAsync(final, offset, count, token), ct);
                return new(final, extension, length, true, entries, null);
            }
            catch (Exception ex) when (ex is InvalidDataException or HyperVOperationException or IOException)
            {
                return new(final, extension, length, true, null, "The zip's central directory could not be read before downloading (" + ex.Message + "); it is checked after the download.");
            }
        }
        if (response.StatusCode == HttpStatusCode.OK)
            return new(final, extension, response.Content.Headers.ContentLength, false, null,
                "The server ignores range requests: an interrupted download restarts, and the archive is inspected after it completes.");
        throw Failure(response, final);
    }

    private async Task<byte[]> RangeAsync(Uri uri, long offset, int count, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);
        using var response = await SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.PartialContent) throw new InvalidDataException($"The server answered a range request with HTTP {(int)response.StatusCode}.");
        var bytes = await ReadBodyAsync(response, count, ct);
        return bytes.Length == count ? bytes : throw new InvalidDataException($"A range request returned {bytes.Length} of {count} bytes.");
    }

    /// <summary>
    /// Downloads <paramref name="uri"/> into <paramref name="partial"/>, resuming from its current length, and verifies
    /// the SHA-256 of the whole file. The caller renames the verified file; on a mismatch the file is deleted here.
    /// </summary>
    public async Task<ImageDownload> DownloadAsync(Uri uri, string partial, long? length, string expectedSha256, Action<string, long, long?> progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (length is long known && existing > known) { File.Delete(partial); existing = 0; }
        if (existing > 0)
        {
            await using var previous = ImageFiles.OpenRead(partial);
            var have = existing;
            existing = await ImageFiles.HashStreamAsync(previous, hash, existing, done => progress("verifying the partial download", done, have), ct);
        }
        var resumedFrom = existing;
        long received = 0;
        if (length is not long complete || existing != complete)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
            using var response = await SendAsync(request, ct);
            var mode = FileMode.Create;
            var satisfied = false;
            if (existing > 0 && response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != existing)
                    throw new HyperVOperationException($"The server answered the resume request from byte {existing} with range '{range}'.", "DownloadFailed");
                length ??= range.Length;
                mode = FileMode.Append;
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                if (existing > 0) { hash.GetHashAndReset(); resumedFrom = 0; existing = 0; }
                length ??= response.Content.Headers.ContentLength;
            }
            else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0 && response.Content.Headers.ContentRange?.Length == existing)
            {
                length = existing;
                satisfied = true;
            }
            else throw Failure(response, uri);

            if (!satisfied)
            {
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = new FileStream(partial, mode, FileAccess.Write, FileShare.Read, ImageFiles.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                received = await ReceiveAsync(source, target, hash, existing, length, progress, ct);
            }
        }
        var total = resumedFrom + received;
        if (length is long expected && total != expected)
            throw new HyperVOperationException($"The download ended after {total} of {expected} bytes. The partial file is kept; fetch again to resume.", "DownloadInterrupted");
        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            ImageFiles.TryDelete(partial);
            throw new HyperVOperationException($"SHA-256 mismatch: the catalog lists {expectedSha256} but the download hashes to {actual}. The download was deleted.", "HashMismatch");
        }
        return new(total, received, resumedFrom, actual);
    }

    private async Task<long> ReceiveAsync(Stream source, FileStream target, IncrementalHash hash, long offset, long? length, Action<string, long, long?> progress, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ImageFiles.BufferSize);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long received = 0;
        try
        {
            while (true)
            {
                stall.CancelAfter(stallTimeout);
                int read;
                try { read = await source.ReadAsync(buffer.AsMemory(0, ImageFiles.BufferSize), stall.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new HyperVOperationException($"The download stalled: no data for {stallTimeout.TotalSeconds:0} s after {offset + received} bytes. "
                        + "The partial file is kept; fetch again to resume.", "DownloadInterrupted");
                }
                catch (Exception ex) when ((ex is IOException || ex is HttpRequestException) && !ct.IsCancellationRequested)
                {
                    throw new HyperVOperationException($"The download was interrupted after {offset + received} bytes ({ex.Message}). "
                        + "The partial file is kept; fetch again to resume.", "DownloadInterrupted");
                }
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                hash.AppendData(buffer, 0, read);
                received += read;
                progress("downloading", offset + received, length);
            }
            await target.FlushAsync(ct);
            return received;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headers.CancelAfter(HeaderTimeout);
        try { return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HyperVOperationException($"No response from {request.RequestUri?.Host} within {HeaderTimeout.TotalSeconds:0} s.", "DownloadInterrupted");
        }
        catch (HttpRequestException ex)
        {
            throw new HyperVOperationException($"Cannot download {request.RequestUri}: {ex.Message}", "DownloadFailed");
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while (memory.Length < limit && (read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - memory.Length)), ct)) > 0)
            memory.Write(buffer, 0, read);
        return memory.ToArray();
    }

    private static HyperVOperationException Failure(HttpResponseMessage response, Uri uri) =>
        new($"Download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase} from {uri}.", response.StatusCode == HttpStatusCode.NotFound ? "NotFound" : "DownloadFailed");
}
