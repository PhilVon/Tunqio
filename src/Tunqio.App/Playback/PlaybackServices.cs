using Microsoft.Extensions.DependencyInjection;
using Tunqio.Core.Playback;

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
        return services;
    }
}
