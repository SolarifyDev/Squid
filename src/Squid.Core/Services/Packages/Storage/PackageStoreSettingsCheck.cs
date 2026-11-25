using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Message.Models.PackageStorage;

namespace Squid.Core.Services.Packages.Storage;

/// <summary>
/// The one rule set for unsaved settings of a store kind, with no database and no container: <see cref="PackageStoreService"/> (create, update, test)
/// and the first-run setup, which runs before the database exists, both go through it.
/// </summary>
public static class PackageStoreSettingsCheck
{
    public static IPackageBlobStoreProvider ResolveProvider(IEnumerable<IPackageBlobStoreProvider> providers, string kind)
    {
        return providers.FirstOrDefault(p => p.Kind == kind) ?? throw new PackageStoreProviderNotFoundException(kind);
    }

    /// <summary>
    /// The one place settings are checked and combined: the descriptor's rules, then the provider's own (<see cref="IPackageBlobStoreProvider.ValidateConfiguration"/>),
    /// and the secrets after merging the reusable stored ones. Returns the configuration to save or test, or throws with every problem found.
    /// </summary>
    public static PackageBlobStoreConfiguration PrepareConfiguration(IPackageBlobStoreProvider provider, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> secrets, StoredSecrets stored)
    {
        properties ??= new Dictionary<string, string>();
        secrets ??= new Dictionary<string, string>();

        var problems = PackageStoreSettings.Validate(provider.Describe(), properties, secrets, stored);
        var configuration = new PackageBlobStoreConfiguration { Properties = properties, Secrets = PackageStoreSettings.MergeSecrets(stored, secrets) };

        if (problems.Count == 0)
            problems.AddRange(provider.ValidateConfiguration(configuration));

        if (problems.Count > 0)
            throw new PackageStoreConfigurationInvalidException(provider.Kind, problems);

        return configuration;
    }

    /// <summary>Prepares the settings (throws when invalid), runs the provider's test and adds the provider's warnings about them to the result.</summary>
    public static async Task<PackageBlobStoreTestResult> TestAsync(IPackageBlobStoreProvider provider, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> secrets, StoredSecrets stored, CancellationToken ct)
    {
        var configuration = PrepareConfiguration(provider, properties, secrets, stored);
        var result = await provider.TestAsync(configuration, ct).ConfigureAwait(false);

        result.Warnings = BuildWarnings(provider, configuration);

        return result;
    }

    public static List<string> BuildWarnings(IPackageBlobStoreProvider provider, PackageBlobStoreConfiguration configuration)
    {
        try
        {
            return provider is IPackageBlobStoreWarnings source ? source.GetWarnings(configuration).ToList() : new List<string>();
        }
        catch (Exception ex)
        {
            return [$"The saved settings are invalid: {ex.Message}"];
        }
    }
}
