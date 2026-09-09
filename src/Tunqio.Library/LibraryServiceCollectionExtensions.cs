using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Tunqio.Core;
using Tunqio.Library.Database;

namespace Tunqio.Library;

/// <summary>DI registration for the library layer (docs/solution-structure.md, "Dependency injection").</summary>
public static class LibraryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAppPaths"/>, <see cref="ISettingsStore"/> and <see cref="LibraryDatabase"/> as singletons.
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
        return services;
    }
}
