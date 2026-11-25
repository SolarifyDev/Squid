using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Message.Models.PackageStorage;

namespace Squid.Core.Services.Packages.Storage;

/// <summary>
/// Validates and tests unsaved settings of a store kind against a provider, with no database and no container: the package store service and the
/// first-run setup (which runs before the database exists) both call these.
/// </summary>
public static class PackageStoreSettingsCheck
{
    public static IPackageBlobStoreProvider ResolveProvider(IEnumerable<IPackageBlobStoreProvider> providers, string kind)
    {
        return providers.FirstOrDefault(p => p.Kind == kind) ?? throw new PackageStoreProviderNotFoundException(kind);
    }

    public static void EnsureValid(IPackageBlobStoreProvider provider, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> secrets, IReadOnlyDictionary<string, string> storedSecrets)
    {
        var problems = PackageStoreSettings.Validate(provider.Describe(), properties ?? new Dictionary<string, string>(), secrets ?? new Dictionary<string, string>(), storedSecrets);

        if (problems.Count > 0)
            throw new PackageStoreConfigurationInvalidException(provider.Kind, problems);
    }

    public static Task<PackageBlobStoreTestResult> TestAsync(IPackageBlobStoreProvider provider, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> secrets, IReadOnlyDictionary<string, string> storedSecrets, CancellationToken ct)
    {
        EnsureValid(provider, properties, secrets, storedSecrets);

        var configuration = new PackageBlobStoreConfiguration { Properties = properties ?? new Dictionary<string, string>(), Secrets = PackageStoreSettings.MergeSecrets(storedSecrets, secrets ?? new Dictionary<string, string>()) };

        return provider.TestAsync(configuration, ct);
    }
}
