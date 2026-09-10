using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.Core.Library;

namespace Tunqio.App.Library;

/// <summary>One tag of the cloud: the genre, its label with the count, and the font size its count earns.</summary>
public sealed record GenreTag(GenreDto Genre, string Label, double FontSize);

/// <summary>
/// The Genres view (docs/ui-screens-and-flows.md): a tag cloud sized by track count; a tag opens the Tracks
/// view filtered by that genre.
/// </summary>
public sealed partial class GenresViewModel : ObservableObject
{
    public const double MinFontSize = 13;
    public const double MaxFontSize = 30;

    private readonly IGenreRepository _genres;
    private readonly ILibraryNavigator _navigator;

    [ObservableProperty]
    public partial IReadOnlyList<GenreTag> Tags { get; set; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public GenresViewModel(IGenreRepository genres, ILibraryNavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(genres);
        ArgumentNullException.ThrowIfNull(navigator);
        _genres = genres;
        _navigator = navigator;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IReadOnlyList<GenreDto> genres = await _genres.ListAsync(ct);
        GenreDto[] present = genres.Where(g => g.TrackCount > 0).ToArray();
        int max = present.Length == 0 ? 0 : present.Max(g => g.TrackCount);
        Tags = present.Select(g => new GenreTag(g, g.Name + " (" + g.TrackCount.ToString(CultureInfo.InvariantCulture) + ")", FontSizeFor(g.TrackCount, max))).ToArray();
        IsEmpty = Tags.Count == 0;
    }

    public void Open(GenreTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        _navigator.OpenTracks(TracksSpec.Genre(tag.Genre));
    }

    /// <summary>Logarithmic in the count so one huge genre does not flatten the rest; the largest genre gets <see cref="MaxFontSize"/>.</summary>
    public static double FontSizeFor(int count, int maxCount)
    {
        if (count <= 0 || maxCount <= 0)
        {
            return MinFontSize;
        }

        double share = Math.Log(1 + count) / Math.Log(1 + maxCount);
        return Math.Round(MinFontSize + (MaxFontSize - MinFontSize) * Math.Clamp(share, 0, 1), 1);
    }
}

/// <summary>One library folder in the Folders view.</summary>
public sealed record FolderRow(LibraryFolderDto Folder, string Name, string Detail);

/// <summary>
/// The Folders view (docs/ui-screens-and-flows.md): the watched library folders; a folder opens the Tracks
/// view filtered to it. Folders are added and removed in Settings › Library (E3-S12).
/// </summary>
public sealed partial class FoldersViewModel : ObservableObject
{
    private readonly ILibraryFolderRepository _folders;
    private readonly ILibraryNavigator _navigator;

    [ObservableProperty]
    public partial IReadOnlyList<FolderRow> Rows { get; set; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public FoldersViewModel(ILibraryFolderRepository folders, ILibraryNavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(navigator);
        _folders = folders;
        _navigator = navigator;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IReadOnlyList<LibraryFolderDto> folders = await _folders.ListAsync(ct);
        Rows = folders.Select(f => new FolderRow(f, NameOf(f.Path), Describe(f))).ToArray();
        IsEmpty = Rows.Count == 0;
    }

    public void Open(FolderRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _navigator.OpenTracks(TracksSpec.Folder(row.Folder));
    }

    /// <summary>The last path segment ("Music" for <c>D:\Music\</c>), or the root itself for a drive.</summary>
    public static string NameOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string trimmed = path.TrimEnd('\\', '/');
        string name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }

    private static string Describe(LibraryFolderDto folder)
    {
        var parts = new List<string> { folder.Path };
        if (!folder.Enabled)
        {
            parts.Add("disabled");
        }
        else if (folder.LastScanStatus is { Length: > 0 } status)
        {
            parts.Add("last scan " + status);
        }

        return string.Join(" \u00B7 ", parts);
    }
}
