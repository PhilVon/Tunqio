using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.Library;

/// <summary>
/// One row of the album's track list, with its cells preformatted; <see cref="Artists"/> is empty when the track's
/// credit is the album artist. <see cref="Stars"/> is the one cell that changes under the row (E6-S7), so it
/// notifies; the rest are read once.
/// </summary>
public sealed class AlbumTrackRow : ObservableObject
{
    private int _stars;

    public AlbumTrackRow(TrackDto track, string number, string title, string artists, string duration, int stars)
    {
        ArgumentNullException.ThrowIfNull(track);
        Track = track;
        Number = number;
        Title = title;
        Artists = artists;
        Duration = duration;
        _stars = stars;
    }

    public TrackDto Track { get; }

    public string Number { get; }

    public string Title { get; }

    public string Artists { get; }

    public string Duration { get; }

    /// <summary>
    /// What the row announces (docs/ui-screens-and-flows.md, "Accessibility contract"): the four columns a Tracks row
    /// names, never the rating. Needed since the row became a class for <see cref="Stars"/>'s sake: without it the
    /// row falls back to its type name, where the record it replaced at least read out its fields.
    /// </summary>
    public string AutomationName => Format.TrackRowName(Title, Track.ArtistNames, Track.AlbumTitle, Track.DurationMs);

    /// <summary>The rating as 0..5 stars, for the row's star control.</summary>
    public int Stars
    {
        get => _stars;
        set => SetProperty(ref _stars, value);
    }
}

/// <summary>The tracks of one disc; <see cref="Title"/> is the group header ("Disc 2").</summary>
public sealed record DiscGroup(int? DiscNo, string Title, IReadOnlyList<AlbumTrackRow> Rows);

/// <summary>
/// Album detail (docs/ui-screens-and-flows.md, "Album detail"): the header facts, the tracks grouped by disc,
/// and the actions. Play hands the session the whole album in disc/track order; playing from a row keeps the
/// rows before it behind the current item.
/// </summary>
public sealed partial class AlbumDetailViewModel : ObservableObject
{
    private readonly IAlbumRepository _albums;
    private readonly IPlaybackCommands _playback;
    private readonly ILibraryNavigator _navigator;
    private readonly IFileRevealer _revealer;
    private readonly ITrackRater _rater;
    private bool _listening;

    [ObservableProperty]
    public partial AlbumDto? Album { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ArtistName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasArtistLink { get; set; }

    [ObservableProperty]
    public partial string Subline { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ArtHash { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<AlbumTrackRow> Rows { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<DiscGroup> Discs { get; set; } = [];

    [ObservableProperty]
    public partial bool HasMultipleDiscs { get; set; }

    [ObservableProperty]
    public partial bool NotFound { get; set; }

    public AlbumDetailViewModel(IAlbumRepository albums, IPlaybackCommands playback, ILibraryNavigator navigator, IFileRevealer revealer, ITrackRater rater)
    {
        ArgumentNullException.ThrowIfNull(albums);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(revealer);
        ArgumentNullException.ThrowIfNull(rater);
        _albums = albums;
        _playback = playback;
        _navigator = navigator;
        _revealer = revealer;
        _rater = rater;
    }

    public bool CanPlay => Rows.Count > 0;

    /// <summary>A row's stars were set (E6-S7); the rater writes the row and the stars follow through its event.</summary>
    public Task RateAsync(AlbumTrackRow row, int stars, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        return _rater.RateAsync(row.Track.Id, stars, ct);
    }

    /// <summary>Whether ratings landing anywhere in the app patch the rows here; the page turns it on while it is in the tree.</summary>
    public void ListenForRatings(bool listen)
    {
        if (listen == _listening)
        {
            return;
        }

        _listening = listen;
        if (listen)
        {
            _rater.Changed += OnRatingChanged;
        }
        else
        {
            _rater.Changed -= OnRatingChanged;
        }
    }

    private void OnRatingChanged(object? sender, RatingChange change)
    {
        foreach (AlbumTrackRow row in Rows)
        {
            if (row.Track.Id == change.TrackId)
            {
                row.Stars = Ratings.Stars(change.Rating);
            }
        }
    }

    public async Task LoadAsync(long albumId, CancellationToken ct = default)
    {
        AlbumDetailDto? detail = await _albums.GetDetailAsync(albumId, ct);
        if (detail is null)
        {
            NotFound = true;
            Title = "Album not found";
            return;
        }

        AlbumDto album = detail.Album;
        Album = album;
        Title = album.Title;
        ArtistName = album.AlbumArtist ?? string.Empty;
        HasArtistLink = album.AlbumArtistId is not null;
        ArtHash = album.ArtHash;
        Subline = string.Join(" · ", new[] { Format.Year(album.Year), string.Join(", ", detail.Genres) }.Where(s => s.Length > 0));
        Summary = Summarise(album, detail.Tracks);
        Rows = detail.Tracks.Select(t => Row(t, album.AlbumArtist)).ToArray();
        Discs = GroupByDisc(Rows);
        HasMultipleDiscs = Discs.Count > 1;
        OnPropertyChanged(nameof(CanPlay));
    }

    public Task PlayAsync(CancellationToken ct = default) => CanPlay ? _playback.PlayNowAsync(Ids(), ct: ct) : Task.CompletedTask;

    public Task ShuffleAsync(CancellationToken ct = default) => CanPlay ? _playback.PlayNowAsync(Ids(), shuffle: true, ct: ct) : Task.CompletedTask;

    public Task PlayNextAsync(CancellationToken ct = default) => CanPlay ? _playback.PlayNextAsync(Ids(), ct) : Task.CompletedTask;

    public Task EnqueueAsync(CancellationToken ct = default) => CanPlay ? _playback.EnqueueAsync(Ids(), ct) : Task.CompletedTask;

    /// <summary>Plays the album from <paramref name="row"/>: every track is queued, the current item is this one.</summary>
    public Task PlayFromAsync(AlbumTrackRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        int index = Rows.ToList().FindIndex(r => r.Track.Id == row.Track.Id);
        return _playback.PlayNowAsync(Ids(), Math.Max(0, index), ct: ct);
    }

    /// <summary>The row menu and keyboard actions over the selected rows (list order) with the row invoked on.</summary>
    public Task HandleAsync(TrackAction action, IReadOnlyList<AlbumTrackRow> rows, AlbumTrackRow? anchor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return Task.CompletedTask;
        }

        AlbumTrackRow subject = anchor ?? rows[0];
        long[] ids = rows.Select(r => r.Track.Id).ToArray();
        switch (action)
        {
            case TrackAction.Play:
                // A single row plays the album from there; a multi-row selection plays just the selection.
                return rows.Count == 1 ? PlayFromAsync(subject, ct) : _playback.PlayNowAsync(ids, ct: ct);
            case TrackAction.PlayNext:
                return _playback.PlayNextAsync(ids, ct);
            case TrackAction.Enqueue:
                return _playback.EnqueueAsync(ids, ct);
            case TrackAction.OpenArtist:
                if (subject.Track.Artists.Count > 0)
                {
                    _navigator.OpenArtist(subject.Track.Artists[0].Id);
                }

                return Task.CompletedTask;
            case TrackAction.ShowInFolder:
                _revealer.Reveal(subject.Track.Path);
                return Task.CompletedTask;
            case TrackAction.OpenAlbum:
                return Task.CompletedTask; // already here
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "unknown track action");
        }
    }

    public void ShowInFolder()
    {
        if (Rows.Count > 0)
        {
            _revealer.Reveal(Rows[0].Track.Path);
        }
    }

    public void OpenArtist()
    {
        if (Album?.AlbumArtistId is { } id)
        {
            _navigator.OpenArtist(id);
        }
    }

    /// <summary>"12 tracks · 48 min · 2 discs · FLAC 16/44.1".</summary>
    public static string Summarise(AlbumDto album, IReadOnlyList<TrackDto> tracks)
    {
        ArgumentNullException.ThrowIfNull(album);
        ArgumentNullException.ThrowIfNull(tracks);
        var parts = new List<string> { Format.Tracks(tracks.Count), Format.LongDuration(tracks.Sum(t => (long)t.DurationMs)) };
        int discs = tracks.Select(t => t.DiscNo).Distinct().Count();
        if (discs > 1)
        {
            parts.Add(discs.ToString(CultureInfo.InvariantCulture) + " discs");
        }

        string formats = FormatSummary(tracks);
        if (formats.Length > 0)
        {
            parts.Add(formats);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"FLAC 16/44.1" when the tracks agree on codec, bit depth and rate; "FLAC 24/96" likewise; else the codecs ("FLAC, MP3").</summary>
    public static string FormatSummary(IReadOnlyList<TrackDto> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        string[] codecs = tracks.Select(t => t.Codec.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (codecs.Length != 1)
        {
            return string.Join(", ", codecs);
        }

        int?[] depths = tracks.Select(t => t.BitDepth).Distinct().ToArray();
        int?[] rates = tracks.Select(t => t.SampleRate).Distinct().ToArray();
        if (depths is [{ } depth] && rates is [{ } rate])
        {
            double khz = rate / 1000.0;
            string rateText = khz == Math.Floor(khz) ? khz.ToString("0", CultureInfo.InvariantCulture) : khz.ToString("0.#", CultureInfo.InvariantCulture);
            return string.Create(CultureInfo.InvariantCulture, $"{codecs[0]} {depth}/{rateText}");
        }

        return codecs[0];
    }

    /// <summary>Groups rows by disc number in the order they arrive (disc/track order); all-null discs form one group.</summary>
    public static IReadOnlyList<DiscGroup> GroupByDisc(IReadOnlyList<AlbumTrackRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.GroupBy(r => r.Track.DiscNo)
            .Select(g => new DiscGroup(g.Key, g.Key is { } n ? "Disc " + n.ToString(CultureInfo.InvariantCulture) : "Disc ?", g.ToArray()))
            .ToArray();
    }

    private static AlbumTrackRow Row(TrackDto track, string? albumArtist)
    {
        string artists = track.ArtistNames;
        if (string.Equals(artists, albumArtist, StringComparison.OrdinalIgnoreCase))
        {
            artists = string.Empty;
        }

        return new AlbumTrackRow(track, Format.TrackNo(track.TrackNo), track.Title, artists, Format.Duration(track.DurationMs), Ratings.Stars(track.Rating));
    }

    private long[] Ids() => Rows.Select(r => r.Track.Id).ToArray();
}
