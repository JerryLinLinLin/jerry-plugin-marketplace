using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HyperVControl.Services;

/// <summary>Where a library file came from: a catalog image (with its verified SHA-256) or a registered local file.</summary>
internal sealed record ImageSource(
    string? Catalog = null, string? CatalogLocation = null, string? Image = null, string? Version = null, DateTimeOffset? LastUpdated = null,
    string? Uri = null, string? Sha256 = null, string? ArchiveRelativePath = null, string? RegisteredFrom = null);

/// <summary>Firmware and session settings applied at deploy; null means "use the OS family default".</summary>
internal sealed record ImageConfig(bool? SecureBoot = null, string? EnhancedSessionTransportType = null);

internal sealed record ImageSizes(long? FileBytes = null, long? ArchiveBytes = null);

/// <summary>A VM disk created from a library entry: a differencing child (linked) or a full copy.</summary>
internal sealed record ImageDeployment(string VmName, string? VmId, string DiskPath, bool Linked, DateTimeOffset CreatedAt);

/// <summary>
/// One library entry: a .vhdx or .iso on the host, either fetched from a catalog (the library owns the file)
/// or registered in place. <see cref="ReadOnly"/> records that the library set the file's read-only attribute
/// because it is (or is meant to be) a differencing parent.
/// </summary>
internal sealed record ImageLibraryEntry(
    string Name, string Kind, string OsFamily, string Path, string Format, ImageSource Source, ImageSizes Sizes, ImageConfig Config,
    DateTimeOffset RegisteredAt, bool ReadOnly = false, bool Expired = false, string? Notes = null, string? ArchivePath = null,
    IReadOnlyList<string>? Warnings = null, IReadOnlyList<ImageDeployment>? Deployments = null);

internal sealed record ImageLibraryIndex(int Version, IReadOnlyList<ImageLibraryEntry> Entries)
{
    public ImageLibraryEntry? Find(string name) => Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A catalog location: an http(s) URL, a file: URI, or an absolute local or UNC path to a gallery JSON.</summary>
internal sealed record ImageCatalogSource(string Name, string Location, bool Enabled = true, bool BuiltIn = false);

internal sealed record ImageCatalogIndex(int Version, IReadOnlyList<ImageCatalogSource> Catalogs)
{
    public static ImageCatalogIndex Default => new(1, [new(ImageCatalogFeed.MicrosoftName, ImageCatalogFeed.MicrosoftLocation, true, true)]);
}

/// <summary>
/// The library root on disk: <c>library.json</c>, <c>catalogs.json</c>, the <c>cache</c> directory for downloads,
/// and the image files. Updates are read-modify-write under an in-process gate plus a lock file shared with other
/// MCP processes, and every write replaces the file atomically, so readers never see a partial index.
/// </summary>
internal sealed class ImageLibraryStore(string root)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public string Root { get; } = root;
    public string LibraryFile => Path.Combine(Root, "library.json");
    public string CatalogsFile => Path.Combine(Root, "catalogs.json");
    public string CacheDirectory => Path.Combine(Root, "cache");

    public Task<ImageLibraryIndex> LibraryAsync(CancellationToken ct) =>
        Task.Run(() => Read<ImageLibraryIndex>(LibraryFile) ?? new(1, []), ct);

    /// <summary>The catalog list; the first read writes the default (Microsoft's feed) so users can see and edit it.</summary>
    public async Task<ImageCatalogIndex> CatalogsAsync(CancellationToken ct) =>
        Read<ImageCatalogIndex>(CatalogsFile) ?? await UpdateCatalogsAsync(index => (index, index), ct);

    public Task<T> UpdateLibraryAsync<T>(Func<ImageLibraryIndex, (ImageLibraryIndex Index, T Result)> update, CancellationToken ct) =>
        UpdateAsync(LibraryFile, () => Read<ImageLibraryIndex>(LibraryFile) ?? new(1, []), update, ct);

    public Task<T> UpdateCatalogsAsync<T>(Func<ImageCatalogIndex, (ImageCatalogIndex Index, T Result)> update, CancellationToken ct) =>
        UpdateAsync(CatalogsFile, () => Read<ImageCatalogIndex>(CatalogsFile) ?? ImageCatalogIndex.Default, update, ct);

    private async Task<T> UpdateAsync<TIndex, T>(string file, Func<TIndex> read, Func<TIndex, (TIndex Index, T Result)> update, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await using var crossProcess = await LockAsync(ct);
            var exists = File.Exists(file);
            var current = read();
            var (next, result) = update(current);
            if (!exists || !ReferenceEquals(next, current)) WriteAtomic(file, next!);
            return result;
        }
        finally { Gate.Release(); }
    }

    private async Task<FileStream> LockAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, ".library.lock");
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(100, ct);
            }
        }
    }

    private static void WriteAtomic(string path, object value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, value.GetType(), Json) + "\n", new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static T? Read<T>(string path) where T : class
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<T>(ImageCatalogFeed.Decode(File.ReadAllBytes(path)), Json)
                    ?? throw new HyperVOperationException($"{path} is empty. Fix or delete it; the library never rewrites an unreadable index.", "InvalidLibrary");
            }
            catch (IOException) when (attempt < 20) { Thread.Sleep(50); }
            catch (JsonException ex)
            {
                throw new HyperVOperationException($"{path} is not a valid index ({ex.Message}). Fix or delete it; the library never rewrites an unreadable index.", "InvalidLibrary");
            }
        }
    }
}
