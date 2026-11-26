using Squid.Core.Persistence.Entities.PackageStorage;
using Squid.Message.Enums.PackageStorage;
using Squid.Message.Models.PackageStorage;

namespace Squid.Core.Services.Packages.Storage;

public partial class PackageStoreService
{
    /// <summary>Never throws: a row whose settings cannot be read or resolved still renders, with a warning, so one bad row cannot break the list for everyone.</summary>
    private PackageStoreDto ToDto(PackageStore store)
    {
        var provider = providers.FirstOrDefault(p => p.Kind == store.Kind);
        var warnings = new List<string>();
        var configuration = ReadConfiguration(store, warnings);

        warnings.AddRange(PackageStoreSettingsCheck.BuildWarnings(provider, configuration));

        return new PackageStoreDto
        {
            Id = store.Id,
            Name = store.Name,
            Kind = store.Kind,
            Properties = configuration.Properties.ToDictionary(pair => pair.Key, pair => pair.Value),
            SecretHasValue = BuildSecretHasValue(provider, configuration),
            IsDefault = store.IsDefault,
            Location = BuildLocation(provider, configuration),
            LastTestedDate = store.LastTestedDate,
            LastTestSucceeded = store.LastTestSucceeded,
            LastTestMessage = store.LastTestMessage,
            Warnings = warnings,
            CreatedDate = store.CreatedDate,
            LastModifiedDate = store.LastModifiedDate,
            LastModifiedBy = store.LastModifiedBy
        };
    }

    private static PackageBlobStoreConfiguration ReadConfiguration(PackageStore store, List<string> warnings)
    {
        try
        {
            return PackageStoreConfigurationMapper.ToConfiguration(store);
        }
        catch (Exception ex)
        {
            warnings.Add($"The saved settings are invalid: {ex.Message}");

            return new PackageBlobStoreConfiguration();
        }
    }

    // Keys come from the kind's descriptor, so a secret that was never set still shows up (as false); the values are never read into the DTO.
    private static Dictionary<string, bool> BuildSecretHasValue(IPackageBlobStoreProvider provider, PackageBlobStoreConfiguration configuration)
    {
        var keys = provider?.Describe().Fields.Where(f => f.Kind == PackageStoreFieldKind.Secret).Select(f => f.Key) ?? configuration.Secrets.Keys;

        return keys.ToDictionary(key => key, key => !string.IsNullOrEmpty(configuration.Secret(key)));
    }

    // The provider knows best; failing that, the value of the field the descriptor marks as the location.
    private static string BuildLocation(IPackageBlobStoreProvider provider, PackageBlobStoreConfiguration configuration)
    {
        try
        {
            if (provider is IPackageBlobStoreLocation source) return source.DescribeLocation(configuration);

            var key = provider?.Describe().Fields.FirstOrDefault(f => f.IsLocation)?.Key;

            return key == null ? null : configuration.Property(key);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
