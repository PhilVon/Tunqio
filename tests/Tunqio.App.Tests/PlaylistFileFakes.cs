using Tunqio.App.Library;
using Tunqio.Core.Library;

namespace Tunqio.App.Tests;

/// <summary>M3U8 files without a disk: exports are recorded, imports answer from <see cref="ImportResults"/>.</summary>
internal sealed class FakePlaylistFiles : IPlaylistFiles
{
    public string ExportsDirectory => @"C:\Data\exports\playlists";

    public List<(long PlaylistId, string Path)> Exports { get; } = [];

    /// <summary>What <see cref="ImportAsync"/> returns for a path; a path not here imports as one created playlist of one track.</summary>
    public Dictionary<string, PlaylistImportResult> ImportResults { get; } = [];

    public int TrackCount { get; set; } = 3;

    public Task<PlaylistExportResult?> ExportAsync(long playlistId, string filePath, CancellationToken ct = default)
    {
        Exports.Add((playlistId, filePath));
        return Task.FromResult<PlaylistExportResult?>(new PlaylistExportResult(filePath, TrackCount));
    }

    public Task<IReadOnlyList<PlaylistExportResult>> ExportAllAsync(string directory, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PlaylistExportResult>>([]);

    public Task<PlaylistImportResult> ImportAsync(string filePath, CancellationToken ct = default) =>
        Task.FromResult(ImportResults.TryGetValue(filePath, out PlaylistImportResult? result)
            ? result
            : new PlaylistImportResult(filePath, Path.GetFileNameWithoutExtension(filePath), new PlaylistDto(1, "x", 0, 0, false, 1, 0), 1, 0, false));

    public Task<IReadOnlyList<PlaylistImportResult>> ImportExportsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PlaylistImportResult>>([.. ImportResults.Values]);
}

/// <summary>A picker that answers what the test says, and records what it was asked to suggest.</summary>
internal sealed class FakePlaylistFilePicker : IPlaylistFilePicker
{
    /// <summary>The save path chosen; null is a cancelled picker.</summary>
    public string? SaveAnswer { get; set; }

    public List<string> Suggested { get; } = [];

    public IReadOnlyList<string> OpenAnswer { get; set; } = [];

    public Task<string?> PickSaveFileAsync(string suggestedFileName, CancellationToken ct = default)
    {
        Suggested.Add(suggestedFileName);
        return Task.FromResult(SaveAnswer);
    }

    public Task<IReadOnlyList<string>> PickOpenFilesAsync(CancellationToken ct = default) => Task.FromResult(OpenAnswer);
}
