namespace Tunqio.Core.Playback;

/// <summary>
/// Hover preview (E5-S5): a few seconds of a track under the pointer, mixed after the analysis tap so the visualizer
/// keeps following what is actually playing. <see cref="PlaybackSession"/> implements it, because it is the only
/// caller of the audio engine (ADR-007); the hover controller asks for this and nothing more.
/// </summary>
public interface IPreviewPlayer
{
    /// <summary>
    /// Starts previewing <paramref name="trackId"/> at <see cref="PlaybackSession.PreviewGainDb"/>. A track that is
    /// missing or cannot be opened, or an engine still fading the last preview out, is logged and skipped: a preview
    /// that does not start is not worth telling anyone about.
    /// </summary>
    Task PreviewAsync(long trackId, CancellationToken ct = default);

    /// <summary>Fades the preview out. Nothing happens when nothing is previewing.</summary>
    Task StopPreviewAsync();
}
