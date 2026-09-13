using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Library.Art;
using Tunqio.Library.Database;
using Tunqio.Library.Playlists;
using Tunqio.Library.Repositories;
using Tunqio.Library.Scanning;
using Tunqio.Library.Tags;

namespace Tunqio.Library;

/// <summary>DI registration for the library layer (docs/solution-structure.md, "Dependency injection").</summary>
public static class LibraryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAppPaths"/>, <see cref="ISettingsStore"/>, <see cref="LibraryDatabase"/>, <see cref="ILibraryService"/>, its repositories, <see cref="ITagReader"/>, <see cref="IArtCache"/>, <see cref="ILibraryScanner"/> and <see cref="ILibraryWatcher"/> as singletons.
    /// The database opens (and migrates) on first resolution; the host resolves it during start-up so a recovery
    /// notice can be shown as the window appears. The scanner picks up an <see cref="IDurationProbe"/> (the
    /// engine's slow path) when the host has registered one, and the watcher a <see cref="LibraryWatcherOptions"/>.
    /// The watcher is not started here: the shell starts it once the window is up.
    /// </summary>
    public static IServiceCollection AddLibrary(this IServiceCollection services)
    {
        services.TryAddSingleton<IAppPaths, AppPaths>();
        services.TryAddSingleton<ISettingsStore, JsonSettingsStore>();
        services.TryAddSingleton<IArtCache>(provider => new ArtCache(provider.GetRequiredService<IAppPaths>(), provider.GetService<ILogger<ArtCache>>()));
        services.TryAddSingleton(provider => LibraryDatabase.Open(
            provider.GetRequiredService<IAppPaths>(),
            provider.GetService<TimeProvider>(),
            provider.GetService<ILogger<LibraryDatabase>>()));
        services.TryAddSingleton<ILibraryService>(provider => new LibraryService(
            provider.GetRequiredService<LibraryDatabase>(),
            provider.GetService<TimeProvider>(),
            provider.GetRequiredService<ITagReader>(),
            provider.GetService<IArtCache>(),
            provider.GetService<IDurationProbe>(),
            provider.GetService<ILoggerFactory>(),
            provider.GetService<LibraryWatcherOptions>()));
        services.TryAddSingleton(provider => provider.GetRequiredService<ILibraryService>().Tracks);
        services.TryAddSingleton(provider => provider.GetRequiredService<ILibraryService>().Albums);
        services.TryAddSingleton(provider => provider.GetRequiredService<ILibraryService>().Artists);
        services.TryAddSingleton(provider => provider.GetRequiredService<ILibraryService>().Genres);
        services.TryAddSingleton(provider => provider.GetRequiredService<ILibraryService>().Folders);
        services.TryAddSingleton(provider => provider.GetRequiredService<ILibraryService>().Playlists);
        services.TryAddSingleton(provider => provider.GetRequiredService<ILibraryService>().Search);
        services.TryAddSingleton(provider => provider.GetRequiredService<ILibraryService>().PlayHistory);
        services.TryAddSingleton(provider => provider.GetRequiredService<ILibraryService>().QueueState);
        services.TryAddSingleton(provider => provider.GetRequiredService<ILibraryService>().Scanner);
        services.TryAddSingleton(provider => provider.GetRequiredService<ILibraryService>().Watcher);
        // M3U8 import, export and auto-export (E6-S2). The shell starts the auto-export and flushes it on the way out.
        services.TryAddSingleton(provider => new PlaylistFiles(
            provider.GetRequiredService<IPlaylistRepository>(),
            provider.GetRequiredService<ITrackRepository>(),
            provider.GetRequiredService<IAppPaths>().ExportsDirectory,
            PlaylistFiles.DefaultExportWindow,
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetService<ILogger<PlaylistFiles>>()));
        services.TryAddSingleton<IPlaylistFiles>(provider => provider.GetRequiredService<PlaylistFiles>());
        services.TryAddSingleton<ITagReader>(provider => new TagLibTagReader(provider.GetRequiredService<ISettingsStore>(), provider.GetService<ILogger<TagLibTagReader>>()));
        // The writer, but not the editor: the editor needs to know which file playback is holding open, which is
        // the shell's business, so the shell registers it (Tunqio.App.Library.LibraryViewServices).
        services.TryAddSingleton<ITagWriter>(provider => new TagLibTagWriter(TagWriterOptions.Default, provider.GetService<ILogger<TagLibTagWriter>>()));
        return services;
    }
}
