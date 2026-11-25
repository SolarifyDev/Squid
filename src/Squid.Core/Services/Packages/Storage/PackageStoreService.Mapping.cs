using Squid.Core.Persistence.Entities.PackageStorage;
using Squid.Message.Enums.PackageStorage;
using Squid.Message.Models.PackageStorage;

namespace Squid.Core.Services.Packages.Storage;

public partial class PackageStoreService
{
    private PackageStoreDto ToDto(PackageStore store)
    {
        var configuration = PackageStoreConfigurationMapper.ToConfiguration(store);
        var provider = providers.FirstOrDefault(p => p.Kind == store.Kind);

        return new PackageStoreDto
        {
            Id = store.Id,
            Name = store.Name,
            Kind = store.Kind,
            Properties = configuration.Properties.ToDictionary(pair => pair.Key, pair => pair.Value),
            SecretHasValue = BuildSecretHasValue(provider, configuration),
            IsDefault = store.IsDefault,
            LastTestedDate = store.LastTestedDate,
            LastTestSucceeded = store.LastTestSucceeded,
            LastTestMessage = store.LastTestMessage,
            Warnings = BuildWarnings(provider, configuration),
            CreatedDate = store.CreatedDate,
            LastModifiedDate = store.LastModifiedDate,
            LastModifiedBy = store.LastModifiedBy
        };
    }

    // Keys come from the kind's descriptor, so a secret that was never set still shows up (as false); the values are never read into the DTO.
    private static Dictionary<string, bool> BuildSecretHasValue(IPackageBlobStoreProvider provider, PackageBlobStoreConfiguration configuration)
    {
        var keys = provider?.Describe().Fields.Where(f => f.Kind == PackageStoreFieldKind.Secret).Select(f => f.Key) ?? configuration.Secrets.Keys;

        return keys.ToDictionary(key => key, key => !string.IsNullOrEmpty(configuration.Secret(key)));
    }

    private static List<string> BuildWarnings(IPackageBlobStoreProvider provider, PackageBlobStoreConfiguration configuration)
    {
        return provider is IPackageBlobStoreWarnings source ? source.GetWarnings(configuration).ToList() : new List<string>();
    }
}
