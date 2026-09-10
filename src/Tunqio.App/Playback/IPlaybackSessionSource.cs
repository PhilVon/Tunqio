using Tunqio.Core.Playback;

namespace Tunqio.App.Playback;

/// <summary>
/// Where the app's one <see cref="PlaybackSession"/> can be found once there is one. <see cref="AudioStartup"/> is
/// the implementation; the interface exists because the session is created after the host is built, so everything
/// that needs it — the views' <see cref="IPlaybackCommands"/> today, the transport and Now Playing in E2 — has to
/// ask for it rather than be handed it at registration.
/// </summary>
public interface IPlaybackSessionSource
{
    /// <summary>The session, or null while audio is still coming up and for the whole session when it could not.</summary>
    PlaybackSession? Session { get; }

    /// <summary>True once start-up has run: with a null <see cref="Session"/> it means audio is unavailable, not pending.</summary>
    bool Started { get; }
}
