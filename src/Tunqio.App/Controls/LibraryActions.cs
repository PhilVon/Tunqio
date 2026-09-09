using Tunqio.Core.Library;

namespace Tunqio.App.Controls;

/// <summary>What a user asked of one or more tracks in a list (row double-click, Enter, the row menu).</summary>
public enum TrackAction
{
    /// <summary>Play the tracks now, starting at the anchor.</summary>
    Play,
    PlayNext,
    Enqueue,
    OpenAlbum,
    OpenArtist,
    ShowInFolder,
}

/// <summary>The tracks an action applies to, in list order, and the row it was invoked on (if any).</summary>
public sealed class TrackActionEventArgs : EventArgs
{
    public TrackActionEventArgs(TrackAction action, IReadOnlyList<TrackDto> tracks, TrackDto? anchor)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        Action = action;
        Tracks = tracks;
        Anchor = anchor;
    }

    public TrackAction Action { get; }

    public IReadOnlyList<TrackDto> Tracks { get; }

    public TrackDto? Anchor { get; }
}

/// <summary>The Tracks table's columns (docs/ui-screens-and-flows.md, "Library › Tracks"); <see cref="Title"/> is always shown.</summary>
public enum TrackColumn
{
    Number,
    Title,
    Artist,
    Album,
    Duration,
    Format,
    Plays,
    Rating,
}

/// <summary>What a user asked of an album tile (click, Enter, the tile menu).</summary>
public enum AlbumAction
{
    Play,
    PlayNext,
    Enqueue,
    Open,
    ShowInFolder,
}

public sealed class AlbumActionEventArgs : EventArgs
{
    public AlbumActionEventArgs(AlbumAction action, AlbumDto album)
    {
        ArgumentNullException.ThrowIfNull(album);
        Action = action;
        Album = album;
    }

    public AlbumAction Action { get; }

    public AlbumDto Album { get; }
}

/// <summary>Modifier-key state for the list controls' Enter handling (Enter, Shift+Enter, Ctrl+Enter per the shortcut table).</summary>
internal static class Modifiers
{
    public static bool IsDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    public static bool Shift => IsDown(Windows.System.VirtualKey.Shift);

    public static bool Control => IsDown(Windows.System.VirtualKey.Control);
}

/// <summary>
/// The fire-and-forget seam for event handlers (docs/solution-structure.md, "Error handling policy": no
/// <c>async void</c>; a faulted task is logged, never lost).
/// </summary>
internal static class Fire
{
    public static void Forget(this Task task, string what)
    {
        ArgumentNullException.ThrowIfNull(task);
        _ = task.ContinueWith(
            t => Serilog.Log.Error(t.Exception!, "{What} failed", what),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
