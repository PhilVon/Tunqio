namespace Tunqio.App.Activation;

/// <summary>
/// What <see cref="CommandRouter"/> drives: the existing command surface (the session's transport and
/// <see cref="Tunqio.Library.OpenFilesService"/>) plus the window. A method that cannot do what it was asked throws, and
/// the router turns that into its one log line.
/// </summary>
public interface ICommandTarget
{
    /// <summary>Plays one file now, inserted at the current position with the rest of the queue kept (flow 2).</summary>
    Task PlayFileNowAsync(string file, CancellationToken ct);

    /// <summary>Replaces the queue with what the paths contain and plays.</summary>
    Task PlayPathsAsync(IReadOnlyList<string> paths, CancellationToken ct);

    /// <summary>Appends what the paths contain to the queue.</summary>
    Task QueuePathsAsync(IReadOnlyList<string> paths, CancellationToken ct);

    Task TogglePlayPauseAsync(CancellationToken ct);

    Task NextAsync(CancellationToken ct);

    Task PreviousAsync(CancellationToken ct);

    /// <summary>Plays one library track now, at the current position (a jump list track item, E7-S5). Throws for a track that has gone.</summary>
    Task PlayTrackAsync(long trackId, CancellationToken ct);

    /// <summary>Replaces the queue with a playlist and plays it (a jump list playlist item, E7-S5). Throws for a playlist that has gone or is empty.</summary>
    Task PlayPlaylistAsync(long playlistId, CancellationToken ct);

    /// <summary>Restores and activates the main window and asks for the foreground.</summary>
    void BringToForeground();
}
