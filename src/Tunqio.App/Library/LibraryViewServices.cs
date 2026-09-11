using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tunqio.Core.Library;

namespace Tunqio.App.Library;

/// <summary>DI registration for the library views (docs/solution-structure.md: view models transient; navigation, the scan coordinator and the folder picker singletons).</summary>
public static class LibraryViewServices
{
    /// <param name="uiContext">The XAML thread's context; the scan coordinator raises its events through it.</param>
    public static IServiceCollection AddLibraryViews(this IServiceCollection services, SynchronizationContext? uiContext)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<LibraryNavigator>();
        services.AddSingleton<ILibraryNavigator>(p => p.GetRequiredService<LibraryNavigator>());
        services.AddSingleton<IFileRevealer, ExplorerFileRevealer>();
        services.AddSingleton<ILibraryFolderPicker, WinUiFolderPicker>();
        services.AddSingleton(p => new LibraryScanCoordinator(
            p.GetRequiredService<ILibraryScanner>(), p.GetService<TimeProvider>(), uiContext, p.GetService<ILogger<LibraryScanCoordinator>>()));
        // The tag editor (E3-S10). ActiveTrackFile resolves the editor lazily because the editor asks it which
        // file is open: a constructor dependency both ways would be a cycle.
        services.AddSingleton(p => new ActiveTrackFile(
            p.GetRequiredService<Playback.IPlaybackSessionSource>(), p.GetRequiredService<ITagEditor>));
        services.AddSingleton<ITagEditor>(p => new Tunqio.Library.Tags.TagEditor(
            p.GetRequiredService<ITagWriter>(),
            p.GetRequiredService<ILibraryScanner>(),
            isPlaying: path => p.GetRequiredService<ActiveTrackFile>().IsOpen(path),
            p.GetService<ILogger<Tunqio.Library.Tags.TagEditor>>()));
        services.AddTransient<TagEditorViewModel>();
        services.AddTransient<AlbumActions>();
        services.AddTransient<AlbumsViewModel>();
        services.AddTransient<AlbumDetailViewModel>();
        services.AddTransient<ArtistsViewModel>();
        services.AddTransient<ArtistDetailViewModel>();
        services.AddTransient<TracksViewModel>();
        services.AddTransient<GenresViewModel>();
        services.AddTransient<FoldersViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<LibrarySettingsViewModel>();
        return services;
    }
}
