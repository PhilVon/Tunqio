using Microsoft.Extensions.Logging;
using Tunqio.Core;
using Tunqio.Core.Library;

namespace Tunqio.Library.Tags;

/// <summary>
/// <see cref="ITrackRater"/> over the track repository and the tag writer (E6-S7). The row is written first and
/// <see cref="Changed"/> raised at once, so every view showing the track follows the click; the file is written
/// after, and only when <c>library.writeRatingsToFiles</c> is on (OQ-7: opt-in, default off).
/// </summary>
/// <remarks>
/// <para>
/// The file write goes through <see cref="ITagWriter"/> as a one-field edit, so it gets the same temp-write-verify-
/// replace path the tag editor uses (E3-S10, and the retry at the swap from T-124) rather than a second way of
/// touching a file. It also inherits the editor's problem: the engine holds the playing file open, and the track
/// somebody rates is more often than not the one that is playing. Those writes are deferred here the way the
/// editor defers its own, and <see cref="FlushDeferredAsync"/> writes them when playback lets go of the file.
/// A later rating of the same file replaces the pending one rather than queuing behind it.
/// </para>
/// <para>
/// What this does not do is touch the library after the file write. The tag editor rescans the paths it wrote
/// because the scanner is what turns tags into rows; the rating never comes from the file, so there is nothing a
/// rescan would learn, and the row is already what the user asked for.
/// </para>
/// </remarks>
public sealed class TrackRater : ITrackRater
{
    private readonly ITrackRepository _tracks;
    private readonly ITagWriter _writer;
    private readonly ISettingsStore _settings;
    private readonly Func<string, bool> _isPlaying;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="isPlaying">Whether playback holds the file open now; null treats nothing as playing (the library tests).</param>
    public TrackRater(ITrackRepository tracks, ITagWriter writer, ISettingsStore settings, Func<string, bool>? isPlaying = null, ILogger<TrackRater>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(settings);
        _tracks = tracks;
        _writer = writer;
        _settings = settings;
        _isPlaying = isPlaying ?? (_ => false);
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public event EventHandler<RatingChange>? Changed;

    public event EventHandler<RatingChange>? FileWriteCompleted;

    /// <summary>Files whose rating is waiting for playback to release them.</summary>
    public int DeferredCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Whether ratings go to files as well as the row, read afresh each time so the Settings switch applies at once.</summary>
    public bool WritesToFiles => _settings.GetValue(SettingsKeys.LibraryWriteRatingsToFiles, SettingsKeys.Defaults.LibraryWriteRatingsToFiles);

    public async Task<RatingChange> RateAsync(long trackId, int stars, CancellationToken ct = default)
    {
        int? rating = Ratings.FromStars(stars);
        TrackDto? track = await _tracks.GetAsync(trackId, ct).ConfigureAwait(false);
        if (track is null || !await _tracks.SetRatingAsync(trackId, rating, ct).ConfigureAwait(false))
        {
            _logger.LogWarning("Rating of track {TrackId} not written: the library no longer has it", trackId);
            return new RatingChange(trackId, track?.Path ?? string.Empty, rating, null, "The track is no longer in the library.");
        }

        _logger.LogInformation("Rated track {TrackId} {Rating} ({Stars}) in the library", trackId, rating?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "cleared", Ratings.Describe(stars));
        var change = new RatingChange(trackId, track.Path, rating);
        Changed?.Invoke(this, change);

        if (!WritesToFiles)
        {
            return change;
        }

        if (_isPlaying(track.Path))
        {
            lock (_lock)
            {
                _pending[track.Path] = new Pending(trackId, track.Path, rating);
            }

            _logger.LogDebug("Rating of {Path} deferred: playback holds the file open", track.Path);
            return change with { FileWrite = TagWriteOutcome.Deferred };
        }

        return await WriteFileAsync(change, ct).ConfigureAwait(false);
    }

    public async Task<int> FlushDeferredAsync(CancellationToken ct = default)
    {
        Pending[] ready;
        lock (_lock)
        {
            ready = [.. _pending.Values.Where(p => !_isPlaying(p.Path))];
            foreach (Pending item in ready)
            {
                _pending.Remove(item.Path);
            }
        }

        int written = 0;
        foreach (Pending item in ready)
        {
            ct.ThrowIfCancellationRequested();
            // The switch may have gone off while the write waited; a rating set with it on is still written, because
            // that is what the user asked for at the time and the row already says it.
            RatingChange result = await WriteFileAsync(new RatingChange(item.TrackId, item.Path, item.Rating), ct).ConfigureAwait(false);
            if (result.FileWrite == TagWriteOutcome.Written)
            {
                written++;
            }
        }

        return written;
    }

    /// <summary>Writes the rating into the file and tells the listeners how it went; the row is not touched either way.</summary>
    private async Task<RatingChange> WriteFileAsync(RatingChange change, CancellationToken ct)
    {
        // 0 is the writer's "clear" (TagSnapshot's remarks); null would leave the file's rating alone.
        TagWriteResult result = await _writer.WriteAsync(change.Path, new TagEdit(Rating: change.Rating ?? 0), ct).ConfigureAwait(false);
        RatingChange outcome = change with { FileWrite = result.Outcome, Error = result.Error };
        if (result.Outcome == TagWriteOutcome.Failed)
        {
            _logger.LogWarning("Rating of {Path} could not be written to the file: {Error}. The library rating stands.", change.Path, result.Error);
        }
        else
        {
            _logger.LogInformation("Rating of {Path} written to the file: {Outcome}", change.Path, result.Outcome);
        }

        FileWriteCompleted?.Invoke(this, outcome);
        return outcome;
    }

    private sealed record Pending(long TrackId, string Path, int? Rating);
}
