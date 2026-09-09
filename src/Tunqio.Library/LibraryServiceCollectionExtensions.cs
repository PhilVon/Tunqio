using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Library.Database;
using Tunqio.Library.Repositories;
using Tunqio.Library.Tags;

namespace Tunqio.Library;

/// <summary>DI registration for the library layer (docs/solution-structure.md, "Dependency injection").</summary>
public static class LibraryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAppPaths"/>, <see cref="ISettingsStore"/>, <see cref="LibraryDatabase"/>, <see cref="ILibraryService"/>, its repositories and <see cref="ITagReader"/> as singletons.
    /// The database opens (and migrates) on first resolution; the host resolves it during start-up so a recovery
    /// notice can be shown as the window appears.
    /// </summary>
    public static IServiceCollection AddLibrary(this IServiceCollection services)
    {
        services.TryAddSingleton<IAppPaths, AppPaths>();
        services.TryAddSingleton<ISettingsStore, JsonSettingsStore>();
        services.TryAddSingleton(provider => LibraryDatabase.Open(
            provider.GetRequiredService<IAppPaths>(),
            provider.GetService<TimeProvider>(),
            provider.GetService<ILogger<LibraryDatabase>>()));
        services.TryAddSingleton<ILibraryService>(provider => new LibraryService(provider.GetRequiredService<LibraryDatabase>(), provider.GetService<TimeProvider>()));
        services.TryAddSingleton(provider => provider.GetRequiredService<ILibraryService>().Tracks);
        services.TryAddSingleton(provider => provider.GetRequiredService<ILibraryService>().Albums);
        services.TryAddSingleton(provider => provider.GetRequiredService<ILibraryService>().Artists);
        services.TryAddSingleton(provider => provider.GetRequiredService<ILibraryService>().Genres);
        services.TryAddSingleton(provider => provider.GetRequiredService<ILibraryService>().Folders);
        services.TryAddSingleton<ITagReader>(provider => new TagLibTagReader(provider.GetRequiredService<ISettingsStore>(), provider.GetService<ILogger<TagLibTagReader>>()));
        return services;
    }
}
