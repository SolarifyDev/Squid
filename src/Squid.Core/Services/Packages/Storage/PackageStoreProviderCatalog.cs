using Squid.Core.Services.Packages.Storage.Providers.FileSystem;
using Squid.Core.Settings.Setup;

namespace Squid.Core.Services.Packages.Storage;

/// <summary>
/// The package store providers built straight from configuration, for the first-run setup host, which has no container (and no database) to resolve
/// <see cref="IPackageBlobStoreProvider"/> from. A new provider kind is listed here as well as being auto-registered for the server.
/// </summary>
public static class PackageStoreProviderCatalog
{
    public static IReadOnlyList<IPackageBlobStoreProvider> Create(IConfiguration configuration)
    {
        return new IPackageBlobStoreProvider[] { new FileSystemPackageBlobStoreProvider(new DataDirectorySetting(configuration)) };
    }
}
