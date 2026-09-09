using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.Core;
using Tunqio.Core.Library;

namespace Tunqio.Library.Art;

/// <summary>
/// The hashed on-disk art cache (E3-S7; docs/library-and-data.md "Storage layout" and the ExtractArt stage).
/// <c>art\ab\&lt;sha256&gt;\</c> holds <c>1000.jpg</c>, <c>300.jpg</c>, <c>96.jpg</c>, <c>palette.json</c> and, for a
/// JPEG or PNG source under 4 MB, <c>original.jpg|png</c>. An image is rendered into a temporary directory and
/// moved into place, so a directory that exists is whole (<c>palette.json</c>, written last, is the check) and
/// a crash mid-write leaves nothing a later scan trusts. The same hash in flight twice (ten tracks of one album
/// through the scanner's two art workers) shares one render. The folder-image lookup is memoised per
/// directory against the directory's and the image's stamps.
/// </summary>
public sealed class ArtCache : IArtCache
{
    /// <summary>A source bigger than this is not kept as <c>original.*</c>.</summary>
    public const int OriginalLimit = 4 * 1024 * 1024;

    private const int FolderMemoLimit = 4096;

    private readonly string _root;
    private readonly ILogger _logger;
    private readonly Dictionary<string, Task<bool>> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FolderImage> _folders = new(StringComparer.OrdinalIgnoreCase);
    private int _rendered;

    public ArtCache(IAppPaths paths, ILogger<ArtCache>? logger = null)
        : this((paths ?? throw new ArgumentNullException(nameof(paths))).ArtDirectory, logger)
    {
    }

    public ArtCache(string artDirectory, ILogger<ArtCache>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artDirectory);
        _root = Path.GetFullPath(artDirectory);
        _logger = logger ?? NullLogger<ArtCache>.Instance;
    }

    /// <summary>The <c>art\</c> root.</summary>
    public string Root => _root;

    /// <summary>Images decoded and written by this instance (a cache hit is not one).</summary>
    public int Rendered => Volatile.Read(ref _rendered);

    public async Task<ArtHashes> StoreAsync(EmbeddedPicture? picture, string audioPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(audioPath);
        ct.ThrowIfCancellationRequested();

        if (picture is { Bytes.Length: > 0 })
        {
            string hash = Hash(picture.Bytes.Span);
            if (await EnsureStoredAsync(hash, picture.Bytes, ct).ConfigureAwait(false))
            {
                return new ArtHashes(hash, hash);
            }
        }

        string? directory = Path.GetDirectoryName(audioPath);
        if (directory is null)
        {
            return ArtHashes.None;
        }

        string? folderHash = await FolderHashAsync(directory, ct).ConfigureAwait(false);
        return folderHash is null ? ArtHashes.None : new ArtHashes(null, folderHash);
    }

    public string? PathFor(string? hash, ArtSize size)
    {
        string? key = Normalise(hash);
        return key is null ? null : Path.Combine(DirectoryFor(key), SizeFileName(size));
    }

    public async Task<ArtPalette?> LoadPaletteAsync(string? hash, CancellationToken ct = default)
    {
        string? key = Normalise(hash);
        if (key is null)
        {
            return null;
        }

        string path = Path.Combine(DirectoryFor(key), PaletteFile.Name);
        try
        {
            byte[] json = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            return PaletteFile.Read(json);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        return Task.Run(
            () =>
            {
                _folders.Clear();
                if (!Directory.Exists(_root))
                {
                    return;
                }

                foreach (string shard in Directory.EnumerateDirectories(_root))
                {
                    ct.ThrowIfCancellationRequested();
                    Directory.Delete(shard, recursive: true);
                }
            },
            ct);
    }

    /// <summary>True when the cache holds a whole rendering of <paramref name="hash"/>.</summary>
    public bool Contains(string? hash)
    {
        string? key = Normalise(hash);
        return key is not null && IsComplete(DirectoryFor(key));
    }

    /// <summary>Lower-case SHA-256 hex of the source bytes: the cache key and the value of <c>art_hash</c>.</summary>
    public static string Hash(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string SizeFileName(ArtSize size) => ((int)size).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jpg";

    private static string? Normalise(string? hash)
    {
        if (hash is not { Length: 64 })
        {
            return null;
        }

        foreach (char c in hash)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return null;
            }
        }

        return hash.ToLowerInvariant();
    }

    private string DirectoryFor(string hash) => Path.Combine(_root, hash[..2], hash);

    private static bool IsComplete(string directory) => File.Exists(Path.Combine(directory, PaletteFile.Name));

    private async Task<string?> FolderHashAsync(string directory, CancellationToken ct)
    {
        FolderImage? memo = _folders.GetValueOrDefault(directory);
        DateTime directoryStamp;
        try
        {
            directoryStamp = Directory.GetLastWriteTimeUtc(directory);
        }
        catch (IOException)
        {
            return null;
        }

        if (memo is null || memo.DirectoryStamp != directoryStamp || memo.Changed())
        {
            memo = FolderImage.Locate(directory, directoryStamp);
            if (_folders.Count >= FolderMemoLimit)
            {
                _folders.Clear();
            }

            _folders[directory] = memo;
        }

        if (memo.Path is null)
        {
            return null;
        }

        string? hash = memo.Hash;
        if (hash is not null && IsComplete(DirectoryFor(hash)))
        {
            return hash;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(memo.Path, ct).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            _logger.LogDebug(e, "Folder image {Path} could not be read", memo.Path);
            return null;
        }
        catch (UnauthorizedAccessException e)
        {
            _logger.LogDebug(e, "Folder image {Path} could not be read", memo.Path);
            return null;
        }

        hash = Hash(bytes);
        if (!await EnsureStoredAsync(hash, bytes, ct).ConfigureAwait(false))
        {
            return null;
        }

        memo.Hash = hash;
        return hash;
    }

    /// <summary>Renders <paramref name="bytes"/> under <paramref name="hash"/> unless it is already there; false when the image cannot be decoded.</summary>
    private async Task<bool> EnsureStoredAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        string directory = DirectoryFor(hash);
        if (IsComplete(directory))
        {
            return true;
        }

        Task<bool>? render;
        lock (_inFlight)
        {
            if (!_inFlight.TryGetValue(hash, out render))
            {
                render = RenderAsync(hash, directory, bytes, ct);
                _inFlight[hash] = render;
            }
        }

        try
        {
            return await render.ConfigureAwait(false);
        }
        finally
        {
            lock (_inFlight)
            {
                if (_inFlight.TryGetValue(hash, out Task<bool>? current) && ReferenceEquals(current, render))
                {
                    _inFlight.Remove(hash);
                }
            }
        }
    }

    private async Task<bool> RenderAsync(string hash, string directory, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        string temp = directory + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using ArtImage image = await ArtImage.OpenAsync(bytes, ct).ConfigureAwait(false);
            Directory.CreateDirectory(temp);

            ArtImage.Pixels large = await image.RenderAsync((int)ArtSize.Large, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(temp, SizeFileName(ArtSize.Large)), await ArtImage.EncodeJpegAsync(large, (int)ArtSize.Large, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

            ArtImage.Pixels tile = await image.RenderAsync((int)ArtSize.Tile, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(temp, SizeFileName(ArtSize.Tile)), await ArtImage.EncodeJpegAsync(tile, (int)ArtSize.Tile, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(temp, SizeFileName(ArtSize.Thumbnail)), await ArtImage.EncodeJpegAsync(tile, (int)ArtSize.Thumbnail, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

            if (image.OriginalExtension is { } extension && bytes.Length < OriginalLimit)
            {
                await File.WriteAllBytesAsync(Path.Combine(temp, "original." + extension), bytes.ToArray(), ct).ConfigureAwait(false);
            }

            ArtPalette palette = MedianCut.Extract(tile.Bgra, tile.Width, tile.Height);
            await File.WriteAllBytesAsync(Path.Combine(temp, PaletteFile.Name), PaletteFile.Write(palette), ct).ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            if (Directory.Exists(directory))
            {
                if (IsComplete(directory))
                {
                    Directory.Delete(temp, recursive: true);
                    return true;
                }

                Directory.Delete(directory, recursive: true);
            }

            Directory.Move(temp, directory);
            Interlocked.Increment(ref _rendered);
            _logger.LogDebug("Art {Hash} rendered from {Width}x{Height} {Extension}", hash, image.Width, image.Height, image.OriginalExtension ?? "image");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DeleteQuietly(temp);
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger.LogInformation(e, "Art {Hash} could not be decoded ({Length} bytes); no art recorded", hash, bytes.Length);
            DeleteQuietly(temp);
            return false;
        }
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>What one directory's folder image was when last looked at, and the hash it rendered to.</summary>
    private sealed class FolderImage(string? path, DateTime directoryStamp, long length, DateTime fileStamp)
    {
        public string? Path { get; } = path;

        public DateTime DirectoryStamp { get; } = directoryStamp;

        public string? Hash { get; set; }

        public static FolderImage Locate(string directory, DateTime directoryStamp)
        {
            string? path = FolderArt.Find(directory);
            if (path is null)
            {
                return new FolderImage(null, directoryStamp, 0, default);
            }

            var info = new FileInfo(path);
            return new FolderImage(path, directoryStamp, info.Length, info.LastWriteTimeUtc);
        }

        /// <summary>True when the image file's size or stamp moved since it was located (a replaced <c>folder.jpg</c>).</summary>
        public bool Changed()
        {
            if (Path is null)
            {
                return false;
            }

            var info = new FileInfo(Path);
            return !info.Exists || info.Length != length || info.LastWriteTimeUtc != fileStamp;
        }
    }
}
