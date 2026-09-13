namespace Tunqio.Core.Library;

/// <summary>A playlist written to an M3U8 file: where, and how many tracks it lists.</summary>
public sealed record PlaylistExportResult(string Path, int TrackCount);

/// <summary>
/// One M3U8 file read into the library. <see cref="Created"/> is null when nothing was made: the name was already taken
/// by a playlist (<see cref="AlreadyExists"/>, importing the exports again), or the file listed tracks and the library has
/// none of them. <see cref="Unmatched"/> counts entries that are not in the library, including lines that are not files.
/// </summary>
public sealed record PlaylistImportResult(string Path, string Name, PlaylistDto? Created, int Matched, int Unmatched, bool AlreadyExists);

/// <summary>
/// Playlists to and from M3U8 files (E6-S2, docs/library-and-data.md, "Durability"): export one or all with paths relative
/// to where the file is written, import files the user picks, and import the app's own auto-exports after a database
/// reset.
/// </summary>
public interface IPlaylistFiles
{
    /// <summary>Where auto-export writes: <c>exports\playlists</c> under the data root.</summary>
    string ExportsDirectory { get; }

    /// <summary>Writes the playlist to <paramref name="filePath"/>, replacing the file. Null when there is no such playlist.</summary>
    Task<PlaylistExportResult?> ExportAsync(long playlistId, string filePath, CancellationToken ct = default);

    /// <summary>Writes every playlist into <paramref name="directory"/>, one file each, named for the playlist.</summary>
    Task<IReadOnlyList<PlaylistExportResult>> ExportAllAsync(string directory, CancellationToken ct = default);

    /// <summary>
    /// Makes a playlist from the file, matching each entry to the library by path. A name already in use gets " (2)",
    /// " (3)" and so on: a file the user chose is a playlist they asked for.
    /// </summary>
    Task<PlaylistImportResult> ImportAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Settings › Library › Import playlists from exports: every file in <see cref="ExportsDirectory"/>, skipping a
    /// playlist whose name the library already has, so running it twice does not double the playlists.
    /// </summary>
    Task<IReadOnlyList<PlaylistImportResult>> ImportExportsAsync(CancellationToken ct = default);
}
