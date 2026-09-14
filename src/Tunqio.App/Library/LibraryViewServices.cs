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
        services.AddSingleton<IPlaylistFilePicker, WinUiPlaylistFilePicker>();
        services.AddSingleton(p => new LibraryScanCoordinator(
            p.GetRequiredService<ILibraryScanner>(), p.GetService<TimeProvider>(), uiContext, p.GetService<ILogger<LibraryScanCoordinator>>()));
        // The tag editor (E3-S10). ActiveTrackFile resolves the editor lazily because the editor asks it which
        // file is open: a constructor dependency both ways would be a cycle.
        services.AddSingleton(p => new ActiveTrackFile(
            p.GetRequiredService<Playback.IPlaybackSessionSource>(), p.GetRequiredService<ITagEditor>, p.GetRequiredService<ITrackRater>));
        services.AddSingleton<ITagEditor>(p => new Tunqio.Library.Tags.TagEditor(
            p.GetRequiredService<ITagWriter>(),
            p.GetRequiredService<ILibraryScanner>(),
            isPlaying: path => p.GetRequiredService<ActiveTrackFile>().IsOpen(path),
            p.GetService<ILogger<Tunqio.Library.Tags.TagEditor>>()));
        // Ratings (E6-S7): the library's rater, which writes the row and (when the switch is on) the file through the
        // same writer and the same active-track deferral as the editor, wrapped so its events reach the views on the
        // XAML thread. One for the process, because every page that shows a track follows its Changed event.
        services.AddSingleton<ITrackRater>(p => new UiThreadRater(
            new Tunqio.Library.Tags.TrackRater(
                p.GetRequiredService<ITrackRepository>(),
                p.GetRequiredService<ITagWriter>(),
                p.GetRequiredService<Core.ISettingsStore>(),
                isPlaying: path => p.GetRequiredService<ActiveTrackFile>().IsOpen(path),
                p.GetService<ILogger<Tunqio.Library.Tags.TrackRater>>()),
            uiContext));
        services.AddTransient<TagEditorViewModel>();
        services.AddTransient<AlbumActions>();
        // Hover preview (E5-S5). One for the process: every grid page reports to it, so a pointer leaving one page's
        // tile and the next page's hover are the same controller's to reconcile.
        services.AddSingleton(p =>
        {
            var source = p.GetRequiredService<Playback.IPlaybackSessionSource>();
            var settings = p.GetRequiredService<Core.ISettingsStore>();
            var controller = new HoverPreviewController(
                () => source.Session,
                p.GetRequiredService<Shell.ShellState>(),
                settings,
                p.GetRequiredService<IAlbumRepository>(),
                TimeProvider.System,
                uiContext);
            // The one-time offer (Q-74) is a notice bar; turning previews on from it is the same setting the
            // Settings > Library switch writes.
            controller.OfferRequested += (_, _) => p.GetRequiredService<Shell.ShellNotices>().ShowHoverPreviewOffer(() =>
            {
                settings.SetValue(Core.SettingsKeys.UiHoverPreview, true);
                settings.Flush();
                Serilog.Log.Debug("Hover previews turned on from the offer");
            });
            return controller;
        });
        services.AddTransient<AlbumsViewModel>();
        services.AddTransient<AlbumDetailViewModel>();
        services.AddTransient<ArtistsViewModel>();
        services.AddTransient<ArtistDetailViewModel>();
        services.AddTransient<TracksViewModel>();
        services.AddTransient<GenresViewModel>();
        services.AddTransient<FoldersViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<LibrarySettingsViewModel>();
        services.AddTransient<PlaylistsViewModel>();
        services.AddTransient<PlaylistDetailViewModel>();
        // Curation's dual pane (E5-S4). One for the process: a playlist page names its playlist here before the mode changes.
        services.AddSingleton<CurationViewModel>();
        return services;
    }
}
