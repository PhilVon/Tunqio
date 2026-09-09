using Microsoft.Extensions.DependencyInjection;

namespace Tunqio.App.Library;

/// <summary>DI registration for the library views (docs/solution-structure.md: view models transient, navigation singleton).</summary>
public static class LibraryViewServices
{
    public static IServiceCollection AddLibraryViews(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<LibraryNavigator>();
        services.AddSingleton<ILibraryNavigator>(p => p.GetRequiredService<LibraryNavigator>());
        services.AddSingleton<IFileRevealer, ExplorerFileRevealer>();
        services.AddTransient<AlbumActions>();
        services.AddTransient<AlbumsViewModel>();
        services.AddTransient<AlbumDetailViewModel>();
        services.AddTransient<ArtistsViewModel>();
        services.AddTransient<ArtistDetailViewModel>();
        services.AddTransient<TracksViewModel>();
        services.AddTransient<GenresViewModel>();
        services.AddTransient<FoldersViewModel>();
        return services;
    }
}
