using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.Core.Library;

namespace Tunqio.App.Library;

/// <summary>One file in the dialog's preview list, with what happened to it once the write has run.</summary>
public sealed partial class TagEditorFileRow : ObservableObject
{
    internal TagEditorFileRow(TagEditTarget target)
    {
        Target = target;
        Display = target.Display;
        Path = target.Path;
    }

    internal TagEditTarget Target { get; }

    public string Display { get; }

    public string Path { get; }

    /// <summary>Empty until the write has run; then what happened to this file.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>True for a file the write could not touch, so the list can mark it.</summary>
    [ObservableProperty]
    public partial bool Failed { get; set; }
}

/// <summary>
/// The tag editor dialog (docs/ui-screens-and-flows.md, "Tag editor dialog", and flow 8). One instance serves
/// both shapes: a single track shows its values, a multi-selection shows the values the selection agrees on and
/// leaves the rest blank behind a "(multiple values)" placeholder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only changed fields are written.</b> Every box remembers the text it was loaded with, and a field is part
/// of the edit only when its text differs from that. This is what makes a batch safe: a user who opens twelve
/// tracks to set the album artist must not thereby stamp the first track's title onto the other eleven, and the
/// rule that produces that is the same one that lets a blanked box mean "clear this field".
/// </para>
/// <para>
/// <b>The values come from the files, not from the rows.</b> A tag editor that showed the database's idea of a
/// track would show the scanner's guesses — a single "A, B" artist tag split into two credits, a missing album
/// title replaced by the folder name — and writing those back would turn a guess into a fact in the user's
/// files. The dialog reads the tags.
/// </para>
/// </remarks>
public sealed partial class TagEditorViewModel : ObservableObject
{
    /// <summary>What a box shows when the selection does not agree on that field.</summary>
    public const string MultipleValues = "(multiple values)";

    /// <summary>
    /// What a screen reader is told about the same box (T-123). The placeholder cannot say it: WinUI keeps
    /// PlaceholderTextContentPresenter in the raw automation view, so Narrator never reads "(multiple values)", and it
    /// is the only thing that separates "they all agree, and it is blank" from "they differ, and I will not touch it".
    /// </summary>
    public const string MultipleValuesHelp = "Multiple values. The selected tracks differ on this field, and it is left as it is unless you type in it.";

    private readonly ITagEditor _editor;
    private readonly ITagWriter _writer;
    private Loaded _loaded = Loaded.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Artists { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AlbumTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AlbumArtist { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Year { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TrackNo { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DiscNo { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Genres { get; set; } = string.Empty;

    /// <summary>"Edit tags" or "Edit tags — 12 tracks".</summary>
    [ObservableProperty]
    public partial string Header { get; set; } = "Edit tags";

    /// <summary>True for a multi-selection: the dialog hides the per-track fields' values behind the placeholder.</summary>
    [ObservableProperty]
    public partial bool IsBatch { get; set; }

    /// <summary>True while the write is running; the dialog's buttons and boxes go read-only.</summary>
    [ObservableProperty]
    public partial bool IsWriting { get; set; }

    /// <summary>0 to 1 for the progress bar.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>"Writing 3 of 12" while the batch runs, then the report's summary.</summary>
    [ObservableProperty]
    public partial string ProgressText { get; set; } = string.Empty;

    /// <summary>A message for the dialog's inline error text: a number that will not parse, or a write that failed.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    public TagEditorViewModel(ITagEditor editor, ITagWriter writer)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(writer);
        _editor = editor;
        _writer = writer;
    }

    /// <summary>The files the edit would touch, in the order they were selected — the dialog's preview list.</summary>
    public ObservableCollection<TagEditorFileRow> Files { get; } = [];

    /// <summary>True when at least one box differs from what it was loaded with, so Confirm has something to do.</summary>
    public bool HasChanges => !BuildEdit().IsEmpty;

    /// <summary>Whether Confirm is available.</summary>
    public bool CanConfirm => !IsWriting && Files.Count > 0 && HasChanges;

    /// <summary>Whether there is an error worth a bar. A property and not a function binding: the XAML compiler
    /// generates code that does not compile for a static x:Bind function cast to Visibility.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Whether the progress line has anything to say.</summary>
    public bool HasProgressText => !string.IsNullOrEmpty(ProgressText);

    // ---- which boxes the selection disagrees on ------------------------------------------------------------
    // True while the tracks differ on the field AND the box still holds what it was loaded with, which is exactly
    // when BuildEdit leaves the field alone. Typing makes it false; clearing the box back to blank makes it true
    // again, because a blank that matches the load is still "unchanged", not "clear". A field the selection
    // agrees on, blank or not, is never mixed.

    public bool IsTitleMixed => IsMixed(Field.Title, Title, _loaded.Title);

    public bool IsArtistsMixed => IsMixed(Field.Artists, Artists, _loaded.Artists);

    public bool IsAlbumTitleMixed => IsMixed(Field.AlbumTitle, AlbumTitle, _loaded.AlbumTitle);

    public bool IsAlbumArtistMixed => IsMixed(Field.AlbumArtist, AlbumArtist, _loaded.AlbumArtist);

    public bool IsYearMixed => IsMixed(Field.Year, Year, _loaded.Year);

    public bool IsTrackNoMixed => IsMixed(Field.TrackNo, TrackNo, _loaded.TrackNo);

    public bool IsDiscNoMixed => IsMixed(Field.DiscNo, DiscNo, _loaded.DiscNo);

    public bool IsGenresMixed => IsMixed(Field.Genres, Genres, _loaded.Genres);

    /// <summary>Reads every selected file's tags and fills the boxes with what the selection agrees on.</summary>
    public async Task LoadAsync(IReadOnlyList<TrackDto> tracks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        Files.Clear();
        var snapshots = new List<TagSnapshot>(tracks.Count);
        foreach (TrackDto track in tracks)
        {
            var target = TagEditTarget.For(track);
            Files.Add(new TagEditorFileRow(target));
            // A file the tag library cannot read still appears in the list; it falls back to the row's values so
            // the user is editing something rather than a blank form, and the write will report its own failure.
            snapshots.Add(await _writer.ReadAsync(track.Path, ct).ConfigureAwait(true) ?? FromRow(track));
        }

        IsBatch = tracks.Count > 1;
        Header = IsBatch ? $"Edit tags — {tracks.Count} tracks" : "Edit tags";
        _loaded = Loaded.From(snapshots);

        Title = _loaded.Title;
        Artists = _loaded.Artists;
        AlbumTitle = _loaded.AlbumTitle;
        AlbumArtist = _loaded.AlbumArtist;
        Year = _loaded.Year;
        TrackNo = _loaded.TrackNo;
        DiscNo = _loaded.DiscNo;
        Genres = _loaded.Genres;
        Error = null;
        Progress = 0;
        ProgressText = string.Empty;
        Notify();
    }

    /// <summary>
    /// The edit the boxes describe: every field the user did not touch is <c>null</c> (unchanged), and a field
    /// they emptied is the empty value, which the writer takes as "clear".
    /// </summary>
    public TagEdit BuildEdit() => new(
        Title: Changed(Title, _loaded.Title),
        Artists: ChangedList(Artists, _loaded.Artists),
        AlbumTitle: Changed(AlbumTitle, _loaded.AlbumTitle),
        AlbumArtist: Changed(AlbumArtist, _loaded.AlbumArtist),
        Year: ChangedNumber(Year, _loaded.Year),
        TrackNo: ChangedNumber(TrackNo, _loaded.TrackNo),
        DiscNo: ChangedNumber(DiscNo, _loaded.DiscNo),
        Genres: ChangedList(Genres, _loaded.Genres));

    /// <summary>
    /// Writes the edit, moving the progress bar and marking each file in the preview list as it goes. Returns
    /// the report so the caller can raise the undo notice; null when a box holds something that is not a number.
    /// </summary>
    public async Task<TagEditReport?> ConfirmAsync(CancellationToken ct = default)
    {
        if (Invalid() is { } invalid)
        {
            Error = invalid;
            return null;
        }

        TagEdit edit = BuildEdit();
        if (edit.IsEmpty)
        {
            return null;
        }

        IsWriting = true;
        Error = null;
        Notify();
        try
        {
            var progress = new Progress<TagEditProgress>(OnProgress);
            TagEditReport report = await _editor.ApplyAsync([.. Files.Select(f => f.Target)], edit, progress, ct).ConfigureAwait(true);
            Apply(report);
            return report;
        }
        finally
        {
            IsWriting = false;
            Notify();
        }
    }

    // ---- change plumbing -----------------------------------------------------------------------------------

    partial void OnTitleChanged(string value) => Notify();

    partial void OnArtistsChanged(string value) => Notify();

    partial void OnAlbumTitleChanged(string value) => Notify();

    partial void OnAlbumArtistChanged(string value) => Notify();

    partial void OnYearChanged(string value) => Notify();

    partial void OnTrackNoChanged(string value) => Notify();

    partial void OnDiscNoChanged(string value) => Notify();

    partial void OnGenresChanged(string value) => Notify();

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnProgressTextChanged(string value) => OnPropertyChanged(nameof(HasProgressText));

    private void Notify()
    {
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(CanConfirm));
        // All eight rather than the one that moved: LoadAsync can change _loaded without changing a box's text
        // (blank to blank), and that raises no OnXChanged of its own.
        OnPropertyChanged(nameof(IsTitleMixed));
        OnPropertyChanged(nameof(IsArtistsMixed));
        OnPropertyChanged(nameof(IsAlbumTitleMixed));
        OnPropertyChanged(nameof(IsAlbumArtistMixed));
        OnPropertyChanged(nameof(IsYearMixed));
        OnPropertyChanged(nameof(IsTrackNoMixed));
        OnPropertyChanged(nameof(IsDiscNoMixed));
        OnPropertyChanged(nameof(IsGenresMixed));
    }

    private bool IsMixed(Field field, string current, string loaded) => (_loaded.Mixed & field) != 0 && current == loaded;

    private void OnProgress(TagEditProgress sample)
    {
        Progress = sample.Total == 0 ? 0 : (double)sample.Completed / sample.Total;
        ProgressText = sample.Completed >= sample.Total
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, "Writing {0} of {1}", sample.Completed + 1, sample.Total);
    }

    private void Apply(TagEditReport report)
    {
        foreach (TagEditFileResult result in report.Files)
        {
            TagEditorFileRow? row = Files.FirstOrDefault(f => f.Target == result.Target);
            if (row is null)
            {
                continue;
            }

            row.Failed = result.Outcome == TagWriteOutcome.Failed;
            row.Status = result.Outcome switch
            {
                TagWriteOutcome.Written => "Updated",
                TagWriteOutcome.Unchanged => "Already matched",
                TagWriteOutcome.Deferred => "Waiting for playback to move on",
                _ => result.Error ?? "Failed",
            };
        }

        Progress = 1;
        ProgressText = report.Summary();
        Error = report.Failed > 0 || report.Error is not null
            ? report.Error ?? $"{report.Failed} of {report.Files.Count} files could not be written; the originals were left untouched."
            : null;
    }

    /// <summary>The first box holding something that is not a number, described; null when they are all fine.</summary>
    private string? Invalid()
    {
        foreach ((string text, string name) in new[] { (Year, "Year"), (TrackNo, "Track number"), (DiscNo, "Disc number") })
        {
            if (text.Length > 0 && text != MultipleValues && !int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out _))
            {
                return $"{name} must be a number (or empty to clear it).";
            }
        }

        return null;
    }

    private static string? Changed(string current, string loaded) => current == loaded ? null : current.Trim();

    private static IReadOnlyList<string>? ChangedList(string current, string loaded) =>
        current == loaded ? null : [.. current.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>A blanked number box clears the field, which the writer spells as zero.</summary>
    private static int? ChangedNumber(string current, string loaded)
    {
        if (current == loaded)
        {
            return null;
        }

        return int.TryParse(current.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int value) ? Math.Max(0, value) : 0;
    }

    /// <summary>What to show for a file whose tags could not be read: the library's idea of it.</summary>
    private static TagSnapshot FromRow(TrackDto track) => new(
        Title: track.Title,
        Artists: [.. track.Artists.Select(a => a.Name)],
        AlbumTitle: track.AlbumTitle,
        AlbumArtist: track.AlbumArtist,
        Year: track.Year,
        TrackNo: track.TrackNo,
        DiscNo: track.DiscNo);

    /// <summary>The boxes, as flags, so <see cref="Loaded"/> can say which ones the selection disagreed on.</summary>
    [Flags]
    private enum Field
    {
        None = 0,
        Title = 1,
        Artists = 2,
        AlbumTitle = 4,
        AlbumArtist = 8,
        Year = 16,
        TrackNo = 32,
        DiscNo = 64,
        Genres = 128,
    }

    /// <summary>
    /// The text each box was loaded with; a field the selection disagrees on loads blank and is in
    /// <see cref="Mixed"/>, which is how a blank that means "they differ" is told from a blank they all share.
    /// </summary>
    private sealed record Loaded(
        string Title,
        string Artists,
        string AlbumTitle,
        string AlbumArtist,
        string Year,
        string TrackNo,
        string DiscNo,
        string Genres,
        Field Mixed)
    {
        public static Loaded Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, Field.None);

        public static Loaded From(List<TagSnapshot> snapshots)
        {
            if (snapshots.Count == 0)
            {
                return Empty;
            }

            Field mixed = Field.None;
            return new Loaded(
                Common(snapshots, s => s.Title ?? string.Empty, Field.Title, ref mixed),
                Common(snapshots, s => string.Join("; ", s.Artists ?? []), Field.Artists, ref mixed),
                Common(snapshots, s => s.AlbumTitle ?? string.Empty, Field.AlbumTitle, ref mixed),
                Common(snapshots, s => s.AlbumArtist ?? string.Empty, Field.AlbumArtist, ref mixed),
                Common(snapshots, s => Number(s.Year), Field.Year, ref mixed),
                Common(snapshots, s => Number(s.TrackNo), Field.TrackNo, ref mixed),
                Common(snapshots, s => Number(s.DiscNo), Field.DiscNo, ref mixed),
                Common(snapshots, s => string.Join("; ", s.Genres ?? []), Field.Genres, ref mixed),
                mixed);
        }

        /// <summary>The value they all share, or blank when they differ (the box then shows the placeholder and the
        /// field is added to <paramref name="mixed"/>).</summary>
        private static string Common(List<TagSnapshot> snapshots, Func<TagSnapshot, string> field, Field flag, ref Field mixed)
        {
            string first = field(snapshots[0]);
            if (snapshots.All(s => string.Equals(field(s), first, StringComparison.Ordinal)))
            {
                return first;
            }

            mixed |= flag;
            return string.Empty;
        }

        private static string Number(int? value) => value is > 0 ? value.Value.ToString(CultureInfo.CurrentCulture) : string.Empty;
    }
}
