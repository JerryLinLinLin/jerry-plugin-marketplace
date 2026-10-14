using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace HyperVControl.Services;

/// <summary>
/// Downloads build sources into the image cache: resumable (HTTP range requests against a <c>.partial</c> file, guarded
/// by If-Range), retried on transient failures, checked against the expected SHA-256 when one is known, and kept for
/// later builds. A cached file is reused only when it still matches the expected hash or the advertised size.
/// </summary>
internal sealed class ImageBuildDownloader(HttpClient http)
{
    internal sealed record Result(string Path, long Bytes, string Sha256, Uri FinalUrl, bool Reused);

    private const int MaxAttempts = 5;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Base delay before a resumed attempt; it grows linearly per attempt, capped at 30 seconds.</summary>
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    internal static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true, MaxAutomaticRedirections = 10, AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30), PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HyperVControl", Program.Version));
        return client;
    }

    /// <summary>Reads the <c>SHA256SUMS</c> published next to <paramref name="imageUrl"/> and returns the entry for its file name.</summary>
    public async Task<string?> PublishedSha256Async(Uri imageUrl, CancellationToken ct)
    {
        var sums = new Uri(imageUrl, "SHA256SUMS");
        using var response = await http.GetAsync(sums, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct);
        return ParseSha256Sums(text, Uri.UnescapeDataString(imageUrl.Segments[^1]));
    }

    /// <summary>Parses <c>sha256sum</c> output (<c>&lt;hex&gt; *name</c> or <c>&lt;hex&gt;  name</c>) for one exact file name.</summary>
    internal static string? ParseSha256Sums(string text, string fileName)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space != 64) continue;
            var name = line[(space + 1)..].TrimStart().TrimStart('*');
            if (name == fileName && line[..64].All(Uri.IsHexDigit)) return line[..64].ToLowerInvariant();
        }
        return null;
    }

    public async Task<Result> DownloadAsync(Uri url, string cacheDirectory, string? expectedSha256, IProgress<JobProgress>? progress, string label, CancellationToken ct)
    {
        Directory.CreateDirectory(cacheDirectory);
        var head = await ProbeAsync(url, ct);
        var fileName = SafeFileName(Uri.UnescapeDataString(head.FinalUrl.Segments[^1]));
        var target = Path.Combine(cacheDirectory, fileName);
        var partial = target + ".partial";

        if (File.Exists(target))
        {
            var cachedLength = new FileInfo(target).Length;
            if (expectedSha256 is null && head.Length is { } advertised && advertised != cachedLength) File.Delete(target);
            else
            {
                var cachedHash = await HashAsync(target, progress, label, ct);
                if (expectedSha256 is null || cachedHash == expectedSha256) return new Result(target, cachedLength, cachedHash, head.FinalUrl, true);
                File.Delete(target);
            }
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await TransferAsync(url, partial, head, progress, label, ct);
                break;
            }
            catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested && ex is HttpRequestException or IOException or TimeoutException)
            {
                progress?.Report(new JobProgress(Message: $"{label}: transfer interrupted ({ex.Message}); resuming (attempt {attempt + 1}/{MaxAttempts})"));
                await Task.Delay(TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(30).Ticks, RetryDelay.Ticks * attempt)), ct);
            }
        }

        var length = new FileInfo(partial).Length;
        if (head.Length is { } expectedLength && length != expectedLength)
            throw new IOException($"Download of {fileName} ended at {length} of {expectedLength} bytes.");
        var hash = await HashAsync(partial, progress, label, ct);
        if (expectedSha256 is not null && hash != expectedSha256)
        {
            File.Delete(partial);
            throw new InvalidDataException($"SHA-256 mismatch for {fileName}: expected {expectedSha256}, got {hash}. The download was deleted.");
        }
        File.Move(partial, target);
        return new Result(target, length, hash, head.FinalUrl, false);
    }

    private sealed record Probe(Uri FinalUrl, long? Length, EntityTagHeaderValue? ETag, DateTimeOffset? LastModified);

    private async Task<Probe> ProbeAsync(Uri url, CancellationToken ct)
    {
        using (var head = new HttpRequestMessage(HttpMethod.Head, url))
        using (var response = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (response.IsSuccessStatusCode)
                return new Probe(response.RequestMessage?.RequestUri ?? url, response.Content.Headers.ContentLength, response.Headers.ETag, response.Content.Headers.LastModified);
        }
        // Some servers refuse HEAD; read only the headers of a GET instead.
        using var get = new HttpRequestMessage(HttpMethod.Get, url);
        using var fallback = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
        fallback.EnsureSuccessStatusCode();
        return new Probe(fallback.RequestMessage?.RequestUri ?? url, fallback.Content.Headers.ContentLength, fallback.Headers.ETag, fallback.Content.Headers.LastModified);
    }

    private async Task TransferAsync(Uri url, string partial, Probe head, IProgress<JobProgress>? progress, string label, CancellationToken ct)
    {
        FileStream file;
        try { file = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous); }
        catch (IOException ex) { throw new InvalidOperationException($"The cache file {partial} is in use by another download: {ex.Message}"); }
        await using (file)
        {
            var offset = file.Length;
            if (head.Length is { } known && offset >= known)
            {
                if (offset == known) return;
                offset = 0;
                file.SetLength(0);
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                if (head.ETag is { IsWeak: false } etag) request.Headers.IfRange = new RangeConditionHeaderValue(etag);
                else if (head.LastModified is { } modified) request.Headers.IfRange = new RangeConditionHeaderValue(modified);
            }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                if (head.Length == offset) return;
                file.SetLength(0);
                throw new IOException("The server rejected the resume range; restarting the download.");
            }
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == offset)
                file.Seek(offset, SeekOrigin.Begin);
            else
            {
                // A full response (the server ignored the range or the content changed): start over.
                offset = 0;
                file.SetLength(0);
            }
            var total = head.Length ?? (response.Content.Headers.ContentLength is { } remaining ? offset + remaining : null);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[1 << 20];
            var done = offset;
            var clock = Stopwatch.StartNew();
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(StallTimeout);
                int read;
                try { read = await body.ReadAsync(buffer, stall.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"No data received for {StallTimeout.TotalSeconds:0} s."); }
                if (read == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (clock.ElapsedMilliseconds >= 500)
                {
                    clock.Restart();
                    progress?.Report(new JobProgress(total is > 0 ? Math.Round(100.0 * done / total.Value, 1) : null, done, total, $"{label}: downloading"));
                }
            }
            await file.FlushAsync(ct);
            progress?.Report(new JobProgress(total is > 0 ? 100 : null, done, total, $"{label}: downloaded"));
        }
    }

    internal static async Task<string> HashAsync(string path, IProgress<JobProgress>? progress, string label, CancellationToken ct)
    {
        progress?.Report(new JobProgress(Message: $"{label}: computing SHA-256"));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return safe.Length == 0 ? "download.bin" : safe.Length > 200 ? safe[^200..] : safe;
    }
}
