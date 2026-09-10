using Microsoft.Extensions.DependencyInjection;
using Tunqio.App.Shell;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Library;

namespace Tunqio.App.Playback;

/// <summary>
/// DI registration for playback (E1-S10e). Both are singletons: there is one engine and one
/// <see cref="PlaybackSession"/> per process (ADR-008), and the views' <see cref="IPlaybackCommands"/> is the
/// forwarder that outlives the moment the session is created.
/// </summary>
public static class PlaybackServices
{
    /// <summary>
    /// Registers <see cref="AudioStartup"/> and the <see cref="IPlaybackCommands"/> that forwards to its session.
    /// Nothing native is touched here: the engine is created when the host calls
    /// <see cref="AudioStartup.StartAsync"/> after the first frame.
    /// </summary>
    public static IServiceCollection AddPlayback(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<AudioStartup>();
        services.AddSingleton<IPlaybackSessionSource>(p => p.GetRequiredService<AudioStartup>());
        services.AddSingleton<IPlaybackCommands, AppPlaybackCommands>();

        // Files opened or dropped are playable without being in the library (E2-S4, ADR: transient tracks). The
        // repository everything resolves ids through is the decorated one, registered after AddLibrary so it is
        // the one the container hands out; it asks the library service for the real repository rather than
        // ITrackRepository, which by then is itself.
        services.AddSingleton<TransientTrackStore>();
        services.AddSingleton<ITrackRepository>(p => new TransientAwareTrackRepository(
            p.GetRequiredService<ILibraryService>().Tracks, p.GetRequiredService<TransientTrackStore>()));
        services.AddSingleton(p => new OpenFilesService(
            p.GetRequiredService<IPlaybackCommands>(),
            p.GetRequiredService<ITagReader>(),
            p.GetRequiredService<TransientTrackStore>(),
            p.GetRequiredService<ILibraryService>().Tracks,
            p.GetService<Microsoft.Extensions.Logging.ILogger<OpenFilesService>>()));
        services.AddSingleton<IAudioFilePicker, WinUiAudioFilePicker>();
        services.AddSingleton(p => new OpenCoordinator(
            p.GetRequiredService<OpenFilesService>(),
            p.GetRequiredService<IAudioFilePicker>(),
            p.GetService<Microsoft.Extensions.Logging.ILogger<OpenCoordinator>>()));
        return services;
    }
}
