using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.Core;
using Tunqio.Core.Library;

namespace Tunqio.App.Library;

/// <summary>One entry of a sort or filter chooser; a <c>null</c> value is "all".</summary>
public sealed record FacetOption(object? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Albums grid (docs/ui-screens-and-flows.md, "Library › Albums"): sort (title, artist, year, added,
/// played; the last two open descending), the genre, decade and format chips, and the tile actions. Every
/// change of sort or chip replaces <see cref="Items"/> with a fresh paged source over the new query; the sort
/// is remembered in settings.
/// </summary>
public sealed partial class AlbumsViewModel : ObservableObject
{
    public static readonly IReadOnlyList<FacetOption> SortOptions =
    [
        new(AlbumSort.Title, "Title"),
        new(AlbumSort.Artist, "Artist"),
        new(AlbumSort.Year, "Year"),
        new(AlbumSort.Added, "Date added"),
        new(AlbumSort.Played, "Last played"),
    ];

    private static readonly FacetOption AllGenres = new(null, "All genres");
    private static readonly FacetOption AllDecades = new(null, "All decades");
    private static readonly FacetOption AllFormats = new(null, "All formats");

    private readonly IAlbumRepository _albums;
    private readonly IGenreRepository _genres;
    private readonly AlbumActions _actions;
    private readonly ISettingsStore _settings;
    private bool _ready;

    // Set while the constructor seeds state read out of settings. The seed has to go through the property now
    // that these are partial properties (there is no backing field to assign around the setter), so the change
    // handlers below use this to avoid writing a value straight back to the store it just came from.
    private bool _seeding;

    [ObservableProperty]
    public partial IncrementalItemsSource<AlbumDto>? Items { get; set; }

    [ObservableProperty]
    public partial FacetOption SelectedSort { get; set; } = SortOptions[0];

    [ObservableProperty]
    public partial bool Descending { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<FacetOption> GenreOptions { get; set; } = [AllGenres];

    [ObservableProperty]
    public partial IReadOnlyList<FacetOption> DecadeOptions { get; set; } = [AllDecades];

    [ObservableProperty]
    public partial IReadOnlyList<FacetOption> CodecOptions { get; set; } = [AllFormats];

    [ObservableProperty]
    public partial FacetOption SelectedGenre { get; set; } = AllGenres;

    [ObservableProperty]
    public partial FacetOption SelectedDecade { get; set; } = AllDecades;

    [ObservableProperty]
    public partial FacetOption SelectedCodec { get; set; } = AllFormats;

    /// <summary>True when the library has no albums at all (the "add folders" prompt), never for an empty filter result.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public AlbumsViewModel(IAlbumRepository albums, IGenreRepository genres, AlbumActions actions, ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(albums);
        ArgumentNullException.ThrowIfNull(genres);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(settings);
        _albums = albums;
        _genres = genres;
        _actions = actions;
        _settings = settings;
        string saved = settings.GetValue(SettingsKeys.UiAlbumsSort, SettingsKeys.Defaults.UiAlbumsSort);
        _seeding = true;
        SelectedSort = SortOptions.FirstOrDefault(o => string.Equals(o.Label, saved, StringComparison.OrdinalIgnoreCase)
            || string.Equals(((AlbumSort)o.Value!).ToString(), saved, StringComparison.OrdinalIgnoreCase)) ?? SortOptions[0];
        Descending = OpensDescending((AlbumSort)SelectedSort.Value!);
        _seeding = false;
    }

    public AlbumSort Sort => (AlbumSort)SelectedSort.Value!;

    public bool HasFilter => SelectedGenre.Value is not null || SelectedDecade.Value is not null || SelectedCodec.Value is not null;

    /// <summary>The query the grid is showing.</summary>
    public AlbumQuery Query => new(
        Sort,
        Descending,
        GenreId: SelectedGenre.Value as long?,
        Decade: SelectedDecade.Value as int?,
        Codec: SelectedCodec.Value as string);

    /// <summary>Loads the chip values and the first page.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        IReadOnlyList<GenreDto> genres = await _genres.ListAsync(ct);
        AlbumFacets facets = await _albums.ListFacetsAsync(ct);
        GenreOptions = [AllGenres, .. genres.Where(g => g.TrackCount > 0).Select(g => new FacetOption(g.Id, g.Name))];
        DecadeOptions = [AllDecades, .. facets.Decades.Select(d => new FacetOption(d, Format.Decade(d)))];
        CodecOptions = [AllFormats, .. facets.Codecs.Select(c => new FacetOption(c, c.ToUpperInvariant()))];
        _ready = true;
        await RequeryAsync(ct);
    }

    public Task HandleAsync(AlbumAction action, AlbumDto album, CancellationToken ct = default) => _actions.HandleAsync(action, album, ct);

    /// <summary>Added and Played read newest-first; the rest A to Z.</summary>
    public static bool OpensDescending(AlbumSort sort) => sort is AlbumSort.Added or AlbumSort.Played;

    partial void OnSelectedSortChanged(FacetOption value)
    {
        if (_seeding)
        {
            return;
        }

        _settings.SetValue(SettingsKeys.UiAlbumsSort, ((AlbumSort)value.Value!).ToString().ToLowerInvariant());
        bool descending = OpensDescending((AlbumSort)value.Value!);
        if (Descending != descending)
        {
            Descending = descending; // its change handler requeries
        }
        else
        {
            Requery();
        }
    }

    partial void OnDescendingChanged(bool value) => Requery();

    partial void OnSelectedGenreChanged(FacetOption value) => Requery();

    partial void OnSelectedDecadeChanged(FacetOption value) => Requery();

    partial void OnSelectedCodecChanged(FacetOption value) => Requery();

    private void Requery()
    {
        if (_ready)
        {
            RequeryAsync(CancellationToken.None).Forget("Albums requery");
        }
    }

    private async Task RequeryAsync(CancellationToken ct)
    {
        IncrementalItemsSource<AlbumDto> items = IncrementalItemsSource.Albums(_albums, Query);
        Items = items;
        await items.LoadMoreAsync(ct);
        IsEmpty = items.Count == 0 && !HasFilter;
    }
}
