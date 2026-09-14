using Microsoft.Extensions.Logging;
using TagLib;
using Tunqio.Core.Library;
using TagFile = TagLib.File;

namespace Tunqio.Library.Tags;

/// <summary>The points a write passes through, in order; the test seam injects a failure at each of them.</summary>
internal enum TagWriteStage
{
    /// <summary>The original has been copied to the temp file, nothing has been written to it yet.</summary>
    Copied,

    /// <summary>TagLibSharp has saved the edited tag into the temp file.</summary>
    Saved,

    /// <summary>The temp file has been read back and agrees with the edit.</summary>
    Verified,

    /// <summary>About to swap the temp file in for the original — the one irreversible step.</summary>
    Replacing,
}

/// <summary>
/// <see cref="ITagWriter"/> over TagLibSharp, the counterpart of <see cref="TagLibTagReader"/> and sharing its
/// vocabulary (<see cref="TagValues"/> for normalisation, a timeout around the synchronous parser, every
/// failure returned rather than thrown).
/// <para>
/// <b>Temp-write-verify-replace.</b> The original is copied beside itself, the edit is saved into the copy, the
/// copy is read back and compared with what was asked for, and only then does <c>File.Replace</c> swap it in.
/// Nothing before the replace can damage the original, because nothing before the replace opens the original
/// for writing at all (AC-106). That matters more than it sounds: growing an ID3v2 tag by one byte makes
/// TagLibSharp rewrite every audio frame after it, and an in-place save that died in the middle of that would
/// leave the user with a truncated song and no way back.
/// </para>
/// <para>
/// <b>Why verify.</b> A tagger can save a value the container cannot hold — an ID3v1-only MP3 truncating a long
/// title at 30 characters, a format with no album-artist field quietly dropping one. Reading the copy back and
/// comparing catches that while the original is still the file on disk, so the user is told the write failed
/// rather than discovering later that half of it did not take. The read-back also proves the audio stream still
/// parses, which is the cheap half of "the file plays afterwards" (AC-105).
/// </para>
/// </summary>
public sealed class TagLibTagWriter : ITagWriter
{
    /// <summary>Suffix of the working copy. It is not an audio extension, so a watcher or a scan mid-write ignores it.</summary>
    internal const string TempSuffix = ".tunqio-tagwrite";

    /// <summary>Tries at the swap, including the first. See <see cref="Replace"/> for why there is more than one.</summary>
    internal const int ReplaceAttempts = 5;

    private readonly TagWriterOptions _options;
    private readonly Func<string, string, TagFile> _open;
    private readonly ILogger _logger;
    private readonly Action<TagWriteStage, string>? _onStage;

    public TagLibTagWriter(ILogger<TagLibTagWriter>? logger = null)
        : this(TagWriterOptions.Default, logger)
    {
    }

    public TagLibTagWriter(TagWriterOptions options, ILogger? logger = null)
        : this(options, logger, null, null)
    {
        ArgumentNullException.ThrowIfNull(options);
    }

    /// <param name="open">Opens a file for tagging given its path and the format to read it as; the tests substitute one that throws or lies.</param>
    /// <param name="onStage">Called as each stage completes, with the temp path; the tests throw from it to fail a write on purpose.</param>
    internal TagLibTagWriter(TagWriterOptions options, ILogger? logger, Func<string, string, TagFile>? open, Action<TagWriteStage, string>? onStage)
    {
        _options = options;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        // By mime type rather than by extension, because the working copy deliberately has neither: TagLibSharp
        // picks its parser from the file name, and a copy called ".tunqio-tagwrite" would be a format it has
        // never heard of. "taglib/flac" and friends are the resolver's own names for the parsers.
        _open = open ?? ((path, format) => TagFile.Create(path, "taglib/" + format, ReadStyle.Average));
        _onStage = onStage;
    }

    public async Task<TagSnapshot?> ReadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ct.ThrowIfCancellationRequested();
        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        // With the pictures: this is what the dialog shows, and the art preview is one of them (T-113).
        return await RunAsync(() => Snapshot(path, Format(path), withPictures: true), ct).ConfigureAwait(false) is { Ok: true, Value: var snapshot } ? snapshot : null;
    }

    public async Task<TagWriteResult> WriteAsync(string path, TagEdit edit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(edit);
        ct.ThrowIfCancellationRequested();

        if (!System.IO.File.Exists(path))
        {
            return new TagWriteResult(path, TagWriteOutcome.Failed, null, "file not found");
        }

        string format = Format(path);
        // The pictures are pulled only for an edit that touches them: the snapshot goes on the undo stack for the
        // session, and a text-only batch must not hold every cover it passed over (TagSnapshot's remarks).
        bool withPictures = edit.Pictures is not null;
        Attempt<TagSnapshot> before = await RunAsync(() => Snapshot(path, format, withPictures), ct).ConfigureAwait(false);
        if (!before.Ok)
        {
            _logger.LogWarning("Tag write of {Path} could not read the current tags: {Error}", path, before.Error);
            return new TagWriteResult(path, TagWriteOutcome.Failed, null, before.Error);
        }

        TagSnapshot current = before.Value!;
        TagSnapshot wanted = current.With(edit);
        if (wanted.Matches(current))
        {
            // Nothing to do, and saying so is not a technicality: a batch that sets Album Artist on twelve tracks
            // where four already have it should rewrite eight files, not twelve, and the undo should offer eight.
            return new TagWriteResult(path, TagWriteOutcome.Unchanged, current);
        }

        Attempt<bool> write = await RunAsync(() => WriteIsolated(path, format, edit, wanted, withPictures), ct).ConfigureAwait(false);
        if (!write.Ok)
        {
            _logger.LogWarning("Tag write of {Path} failed: {Error}", path, write.Error);
            return new TagWriteResult(path, TagWriteOutcome.Failed, current, write.Error);
        }

        return new TagWriteResult(path, TagWriteOutcome.Written, current);
    }

    /// <summary>
    /// The whole write, on the pool and inside one try. The temp file is deleted on every path out, so a run of
    /// failures does not litter the user's music folder with working copies.
    /// </summary>
    private bool WriteIsolated(string path, string format, TagEdit edit, TagSnapshot wanted, bool withPictures)
    {
        string temp = path + TempSuffix;
        try
        {
            // Beside the original rather than in %TEMP%: File.Replace needs both files on the same volume, and a
            // library folder on a different drive from the system one is the normal case, not the exotic one.
            System.IO.File.Copy(path, temp, overwrite: true);
            _onStage?.Invoke(TagWriteStage.Copied, temp);

            using (TagFile file = _open(temp, format))
            {
                Apply(file, edit);
                file.Save();
            }

            _onStage?.Invoke(TagWriteStage.Saved, temp);

            Verify(temp, format, wanted, withPictures);
            _onStage?.Invoke(TagWriteStage.Verified, temp);

            _onStage?.Invoke(TagWriteStage.Replacing, temp);
            Replace(temp, path);
            return true;
        }
        finally
        {
            Discard(temp);
        }
    }

    /// <summary>
    /// Swaps the verified copy in, retrying briefly while something else has the original open.
    /// <para>
    /// Replace rather than Delete-then-Move: it is a single directory operation, it keeps the original's ACLs and
    /// creation time, and it cannot leave the user with no file at all if it fails halfway.
    /// </para>
    /// <para>
    /// The retry is for "Unable to remove the file to be replaced", which is what Windows says when a handle on
    /// the destination was opened without <c>FILE_SHARE_DELETE</c> — an indexer, a scanner, an antivirus pass on a
    /// file written a moment ago. It is transient by nature and it is not rare: a twelve-track batch hit it once in
    /// six runs (T-124), and once during an <em>undo</em>, which is worse than during an edit because the user had
    /// asked for their own values back and one file kept the new ones. Retrying costs a few hundred milliseconds in
    /// the worst case and turns most of those into a write that simply works.
    /// </para>
    /// <para>
    /// It is deliberately a mitigation and not a cure: the holder was never identified. It is not this app — the
    /// editor awaits its own rescan before returning, the working copy carries a non-audio suffix so a watcher pass
    /// skips it, and nothing here opens the original for writing at all. Retrying is the right shape for a holder
    /// you do not own and cannot ask to let go. If a file still will not swap after this, the write fails with the
    /// original untouched, which is the guarantee AC-106 is about and this does not weaken it.
    /// </para>
    /// </summary>
    private void Replace(string temp, string path)
    {
        // 20, 40, 80, 160 ms: about a third of a second in total, under a second of a user's patience, and long
        // enough to outlast a scanner reading one file.
        int delay = 20;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                System.IO.File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                if (attempt > 1)
                {
                    _logger.LogDebug("Tag write of {Path} swapped in on attempt {Attempt}", path, attempt);
                }

                return;
            }
            catch (IOException) when (attempt < ReplaceAttempts)
            {
                _logger.LogDebug(
                    "Tag write of {Path}: the file is held open, retrying the swap in {Delay} ms (attempt {Attempt} of {Attempts})",
                    path, delay, attempt, ReplaceAttempts);
                Thread.Sleep(delay);
                delay *= 2;
            }
        }
    }

    /// <summary>Reads the copy back and insists it says what was asked for, and that the audio is still there.</summary>
    private void Verify(string temp, string format, TagSnapshot wanted, bool withPictures)
    {
        using TagFile file = _open(temp, format);
        TagSnapshot written = Snapshot(file, withPictures);
        if (!written.Matches(wanted))
        {
            throw new TagWriteVerificationException($"the file did not keep the edited values (it reports {Describe(written)}, expected {Describe(wanted)})");
        }

        if (file.Properties is not { } properties || properties.Duration <= TimeSpan.Zero)
        {
            throw new TagWriteVerificationException("the audio stream no longer parses after the tag write");
        }
    }

    private void Discard(string temp)
    {
        try
        {
            if (System.IO.File.Exists(temp))
            {
                System.IO.File.Delete(temp);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The original is what matters and it is intact; a left-over working copy is a mess, not a failure.
            _logger.LogWarning(e, "Could not delete the tag-write working copy {Temp}", temp);
        }
    }

    /// <summary>
    /// Applies the edit to a file's tags. <c>null</c> leaves a field alone; the empty value (<c>""</c>, <c>0</c>, an
    /// empty list) clears it, which is what <see cref="TagSnapshot.ToEdit"/> produces for a field that was
    /// absent and therefore what an undo needs. TagLibSharp treats null and an empty array as "no frame". The
    /// text fields go through the combined <see cref="TagFile.Tag"/>; the rating has no field the containers
    /// agree on, so <see cref="TagRatings"/> puts it where each container's own tag keeps one (E6-S7).
    /// </summary>
    private static void Apply(TagFile file, TagEdit edit)
    {
        Tag tag = file.Tag;
        if (edit.Rating is { } rating)
        {
            // A format this cannot rate is left as it is; the verify then reports the rating the file did not keep.
            TagRatings.Write(file, rating > 0 ? rating : null);
        }

        if (edit.Title is { } title)
        {
            tag.Title = Blank(title);
        }

        if (edit.Artists is { } artists)
        {
            tag.Performers = [.. TagValues.Distinct(artists)];
        }

        if (edit.AlbumTitle is { } album)
        {
            tag.Album = Blank(album);
        }

        if (edit.AlbumArtist is { } albumArtist)
        {
            tag.AlbumArtists = Blank(albumArtist) is { } value ? [value] : [];
        }

        if (edit.Year is { } year)
        {
            tag.Year = year > 0 ? (uint)year : 0;
        }

        if (edit.TrackNo is { } trackNo)
        {
            tag.Track = trackNo > 0 ? (uint)trackNo : 0;
        }

        if (edit.DiscNo is { } discNo)
        {
            tag.Disc = discNo > 0 ? (uint)discNo : 0;
        }

        if (edit.Genres is { } genres)
        {
            tag.Genres = [.. TagValues.Distinct(genres)];
        }

        if (edit.Composer is { } composer)
        {
            tag.Composers = Blank(composer) is { } value ? [value] : [];
        }

        if (edit.Comment is { } comment)
        {
            tag.Comment = Blank(comment);
        }

        if (edit.Pictures is { } pictures)
        {
            // The whole set, so an empty list clears every picture and undo puts every one back (T-113).
            tag.Pictures = [.. pictures.Select(ToTagLib)];
        }
    }

    private static Picture ToTagLib(EmbeddedPicture picture) => new()
    {
        Type = (PictureType)(int)picture.Kind,
        MimeType = picture.MimeType ?? "image/jpeg",
        Data = new ByteVector(picture.Bytes.ToArray()),
        Description = string.Empty,
    };

    private static EmbeddedPicture? FromTagLib(IPicture picture)
    {
        byte[]? data = picture.Data?.Data;
        if (data is null || data.Length == 0)
        {
            return null;
        }

        // The codes are ID3's; anything TagLibSharp adds of its own (NotAPicture) is kept as Other.
        int type = (int)picture.Type;
        PictureKind kind = Enum.IsDefined((PictureKind)type) ? (PictureKind)type : PictureKind.Other;
        return new EmbeddedPicture(data, TagValues.Clean(picture.MimeType), kind);
    }

    private TagSnapshot Snapshot(string path, string format, bool withPictures)
    {
        using TagFile file = _open(path, format);
        return Snapshot(file, withPictures);
    }

    /// <summary>
    /// The parser to read a file with, as TagLibSharp's mime-type resolver names it: the extension, lower-cased.
    /// The library's own <c>File.Create(path)</c> does exactly this, which is why the working copy can be opened
    /// as the original's format without its extension.
    /// </summary>
    private static string Format(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    /// <summary>
    /// The editable fields as the file holds them. Unlike the reader this does <em>not</em> split a single
    /// artist or genre value on its separators: the reader splits to guess what a stranger's file meant, but
    /// here the same values are about to be compared with what was written, and a "Simon, Garfunkel" the user
    /// typed as one name must read back as one name or every verify would fail.
    /// </summary>
    private static TagSnapshot Snapshot(TagFile file, bool withPictures)
    {
        Tag tag = file.Tag;
        IReadOnlyList<string> albumArtists = TagValues.Distinct(tag.AlbumArtists);
        IReadOnlyList<string> composers = TagValues.Distinct(tag.Composers);
        // Reading Pictures pulls every picture's bytes out of the lazily parsed tag, hence the switch.
        IReadOnlyList<EmbeddedPicture>? pictures = withPictures
            ? [.. (tag.Pictures ?? []).Select(FromTagLib).OfType<EmbeddedPicture>()]
            : null;
        return new TagSnapshot(
            Title: TagValues.Clean(tag.Title),
            Artists: TagValues.Distinct(tag.Performers),
            AlbumTitle: TagValues.Clean(tag.Album),
            AlbumArtist: albumArtists.Count > 0 ? albumArtists[0] : null,
            Year: Positive(tag.Year),
            TrackNo: Positive(tag.Track),
            DiscNo: Positive(tag.Disc),
            Genres: TagValues.Distinct(tag.Genres),
            Composer: composers.Count > 0 ? string.Join("; ", composers) : null,
            Comment: TagValues.Clean(tag.Comment),
            Rating: TagRatings.Read(file),
            Pictures: pictures);
    }

    /// <summary>
    /// Runs the synchronous parser on the pool with the reader's per-file budget, and turns everything it throws
    /// into a message. Nothing here rethrows except cancellation: a batch of twelve must report twelve verdicts.
    /// </summary>
    private async Task<Attempt<T>> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        Task<Attempt<T>> task = Task.Run(
            () =>
            {
                try
                {
                    return new Attempt<T>(true, work(), null);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    return new Attempt<T>(false, default, e is TagWriteVerificationException ? e.Message : $"{e.GetType().Name}: {e.Message}");
                }
            },
            ct);

        try
        {
            return await task.WaitAsync(_options.EffectiveTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Same bargain as the reader's: the parser cannot be interrupted, so it is abandoned to finish on its
            // own. It is working on the copy, so whatever it does next cannot reach the original.
            _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new Attempt<T>(false, default, $"tag write exceeded {_options.EffectiveTimeout.TotalSeconds:0.#} s");
        }
    }

    private static string Describe(TagSnapshot snapshot) =>
        $"title '{snapshot.Title}', artists '{string.Join("; ", snapshot.Artists ?? [])}', album '{snapshot.AlbumTitle}', " +
        $"album artist '{snapshot.AlbumArtist}', year {snapshot.Year}, track {snapshot.TrackNo}, disc {snapshot.DiscNo}, " +
        $"genres '{string.Join("; ", snapshot.Genres ?? [])}', rating {snapshot.Rating}" +
        (snapshot.Pictures is { } pictures ? $", pictures [{string.Join(", ", pictures.Select(p => $"{p.Kind} {p.Bytes.Length} bytes"))}]" : string.Empty);

    private static string? Blank(string value) => value.Length == 0 ? null : TagValues.Clean(value);

    private static int? Positive(uint value) => value is > 0 and <= int.MaxValue ? (int)value : null;

    private readonly record struct Attempt<T>(bool Ok, T? Value, string? Error);
}

/// <summary>Writer behaviour. The timeout is the reader's, for the same reason: TagLibSharp cannot be interrupted.</summary>
/// <param name="Timeout">Per-file budget after which the write is abandoned; <c>null</c> means <see cref="TagReaderOptions.DefaultTimeout"/>.</param>
public sealed record TagWriterOptions(TimeSpan? Timeout = null)
{
    public static TagWriterOptions Default { get; } = new();

    public TimeSpan EffectiveTimeout => Timeout ?? TagReaderOptions.DefaultTimeout;
}

/// <summary>The read-back after a save did not agree with the edit; the original was left alone.</summary>
public sealed class TagWriteVerificationException : Exception
{
    public TagWriteVerificationException()
    {
    }

    public TagWriteVerificationException(string message)
        : base(message)
    {
    }

    public TagWriteVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
