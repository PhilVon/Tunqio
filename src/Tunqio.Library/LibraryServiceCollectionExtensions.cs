using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tunqio.Core;

namespace Tunqio.Library;

/// <summary>DI registration for the library layer (docs/solution-structure.md, "Dependency injection").</summary>
public static class LibraryServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IAppPaths"/> and <see cref="ISettingsStore"/> as singletons.</summary>
    public static IServiceCollection AddLibrary(this IServiceCollection services)
    {
        services.TryAddSingleton<IAppPaths, AppPaths>();
        services.TryAddSingleton<ISettingsStore, JsonSettingsStore>();
        return services;
    }
}
