using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.FixtureGen;
using Tunqio.Library.Database;
using Tunqio.Library.Repositories;
using Tunqio.Library.Scanning;
using Tunqio.Library.Tags;

namespace Tunqio.Library.Tests.Scanning;

/// <summary>
/// A scanner over a private copy of the fixture library (so tests can change, remove and restore files) and an
/// in-memory database, with the tag reader and the track repository wrapped so a test can count reads, hold a
/// read, or cancel in the middle of a batch.
/// </summary>
internal sealed class ScanHarness : IDisposable
{
    public const long Now = 1_757_376_000_000; // 2025-09-09T00:00:00Z

    private ScanHarness(string root, LibraryDatabase db, LibraryService service, CountingReader reader, InterceptingTracks tracks, LibraryScanner scanner)
    {
        Root = root;
        Db = db;
        Service = service;
        Reader = reader;
        Tracks = tracks;
        Scanner = scanner;
    }

    /// <summary>The temp directory holding the copied fixture files (no trailing separator).</summary>
    public string Root { get; }

    public LibraryDatabase Db { get; }

    public LibraryService Service { get; }

    public CountingReader Reader { get; }

    public InterceptingTracks Tracks { get; }

    public LibraryScanner Scanner { get; }

    public LibraryFolderDto Folder { get; private set; } = null!;

    public ProgressLog Progress { get; } = new();

    public static string FixtureRoot => RepoPaths.File("tests", "fixtures", "library");

    public static FixtureManifest Manifest() => FixtureManifest.Load(Path.Combine(FixtureRoot, "manifest.json"));

    /// <summary>A harness over a copy of the fixture library (<paramref name="copyFixtures"/>) or an empty root.</summary>
    public static async Task<ScanHarness> CreateAsync(bool copyFixtures = true, IArtCache? artCache = null, IDurationProbe? durationProbe = null, int? readDegree = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "tunqio-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (copyFixtures)
        {
            CopyDirectory(FixtureRoot, root);
        }

        var clock = new LibraryMigratorTests.FixedClock(Now);
        LibraryDatabase db = LibraryDatabase.OpenInMemory(clock: clock);
        var service = new LibraryService(db, clock);
        var reader = new CountingReader(new TagLibTagReader(new TagReaderOptions()));
        var tracks = new InterceptingTracks(service.Tracks);
        var scanner = new LibraryScanner(tracks, service.Folders, reader, clock, artCache, durationProbe, readDegree: readDegree);
        var harness = new ScanHarness(root, db, service, reader, tracks, scanner);
        harness.Folder = await service.Folders.AddAsync(root);
        return harness;
    }

    public Task<ScanReport> ScanAsync(ScanRequest? request = null, CancellationToken ct = default) => Scanner.ScanAsync(request ?? ScanRequest.All, Progress, ct);

    /// <summary>The path of a fixture file inside the copy.</summary>
    public string PathOf(FixtureFileEntry entry) => Path.Combine(Root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    public async Task<IReadOnlyList<TrackDto>> AllTracksAsync(bool includeMissing = false) =>
        await Service.Tracks.ListAsync(new TrackQuery(PageSize: 10_000, IncludeMissing: includeMissing));

    public async Task<TrackDto> TrackAtAsync(string path) =>
        (await AllTracksAsync(includeMissing: true)).Single(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));

    public async Task<LibraryFolderDto> FolderRowAsync() => (await Service.Folders.ListAsync()).Single(f => f.Id == Folder.Id);

    public long Scalar(string sql)
    {
        using SqliteConnection connection = Db.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Rewrites the title tag so the file's size and mtime change the way a real edit would.</summary>
    public static void Retitle(string path, string title)
    {
        using (TagLib.File file = TagLib.File.Create(path))
        {
            file.Tag.Title = title;
            file.Save();
        }

        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2)); // certain even on a coarse file-system clock
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (string directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    public void Dispose()
    {
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A parser abandoned after its timeout may still hold a file; the temp directory is reclaimed later.
        }
    }

    /// <summary>Collects every progress sample synchronously (a <c>Progress&lt;T&gt;</c> would post through xunit's context and arrive late).</summary>
    public sealed class ProgressLog : IProgress<ScanProgress>
    {
        public ConcurrentQueue<ScanProgress> Samples { get; } = new();

        public ScanProgress Last => Samples.Last();

        public void Report(ScanProgress value) => Samples.Enqueue(value);
    }

    /// <summary>Counts reads and lets a test run something before each one (block it, cancel a token).</summary>
    public sealed class CountingReader(ITagReader inner) : ITagReader
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public ConcurrentQueue<string> Paths { get; } = new();

        /// <summary>Runs before each read with the path; may await.</summary>
        public Func<string, CancellationToken, Task>? BeforeRead { get; set; }

        public void Reset()
        {
            Interlocked.Exchange(ref _reads, 0);
            Paths.Clear();
        }

        public async Task<TagReadResult> ReadAsync(string path, long folderId, CancellationToken ct = default)
        {
            if (BeforeRead is { } hook)
            {
                await hook(path, ct);
            }

            Interlocked.Increment(ref _reads);
            Paths.Enqueue(path);
            return await inner.ReadAsync(path, folderId, ct);
        }
    }

    /// <summary>Passes everything through to the real repository and counts batches; a test can act before a batch is written.</summary>
    public sealed class InterceptingTracks(ITrackRepository inner) : ITrackRepository
    {
        private int _batches;

        public int Batches => Volatile.Read(ref _batches);

        /// <summary>Runs before each batch with its ordinal (1-based) and rows.</summary>
        public Action<int, IReadOnlyList<ScannedTrack>>? BeforeBatch { get; set; }

        public Task<TrackDto?> GetAsync(long id, CancellationToken ct = default) => inner.GetAsync(id, ct);

        public Task<IReadOnlyList<TrackDto>> GetByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct = default) => inner.GetByIdsAsync(ids, ct);

        public Task<TrackDto?> GetByPathAsync(string path, CancellationToken ct = default) => inner.GetByPathAsync(path, ct);

        public Task<IReadOnlyList<TrackDto>> ListAsync(TrackQuery query, CancellationToken ct = default) => inner.ListAsync(query, ct);

        public IAsyncEnumerable<TrackDto> StreamAsync(TrackQuery query, CancellationToken ct = default) => inner.StreamAsync(query, ct);

        public Task<int> CountAsync(TrackQuery query, CancellationToken ct = default) => inner.CountAsync(query, ct);

        public Task UpsertBatchAsync(IReadOnlyList<ScannedTrack> tracks, CancellationToken ct = default)
        {
            int ordinal = Interlocked.Increment(ref _batches);
            BeforeBatch?.Invoke(ordinal, tracks);
            return inner.UpsertBatchAsync(tracks, ct);
        }

        public Task MarkMissingAsync(IReadOnlyList<long> ids, bool missing, CancellationToken ct = default) => inner.MarkMissingAsync(ids, missing, ct);

        public Task<int> CountMissingAsync(long missingBefore, CancellationToken ct = default) => inner.CountMissingAsync(missingBefore, ct);

        public Task<int> PurgeMissingAsync(long missingBefore, CancellationToken ct = default) => inner.PurgeMissingAsync(missingBefore, ct);

        public Task<IReadOnlyList<TrackFileStamp>> SnapshotAsync(long folderId, CancellationToken ct = default) => inner.SnapshotAsync(folderId, ct);

        public Task UpdateTagsAsync(long id, TagEdit edit, CancellationToken ct = default) => inner.UpdateTagsAsync(id, edit, ct);
    }
}
