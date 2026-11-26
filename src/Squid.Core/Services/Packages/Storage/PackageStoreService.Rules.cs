using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Message.Commands.PackageStorage;
using Squid.Message.Models.PackageStorage;

namespace Squid.Core.Services.Packages.Storage;

public partial class PackageStoreService
{
    private static string RequireName(string kind, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new PackageStoreConfigurationInvalidException(kind, ["\"Name\" is required"]);

        return name.Trim();
    }

    private async Task EnsureNameFreeAsync(string name, int? exceptId, CancellationToken ct)
    {
        if (await dataProvider.NameExistsAsync(name, exceptId, ct).ConfigureAwait(false))
            throw new PackageStoreNameConflictException(name);
    }

    private static string SerializeProperties(IReadOnlyDictionary<string, string> properties)
    {
        return PackageStoreConfigurationMapper.SerializeSettings(properties) ?? "{}";
    }

    private static StoredSecrets SelectReusable(IPackageBlobStoreProvider provider, IReadOnlyDictionary<string, string> newProperties, Persistence.Entities.PackageStorage.PackageStore stored)
    {
        return PackageStoreSettings.SelectReusable(provider.Describe(), newProperties ?? new Dictionary<string, string>(), PackageStoreConfigurationMapper.ParseSettingsLeniently(stored.Properties), PackageStoreConfigurationMapper.ParseSettingsLeniently(stored.Secret));
    }

    private async Task<StoredSecrets> LoadStoredSecretsAsync(IPackageBlobStoreProvider provider, TestPackageStoreSettingsCommand command, CancellationToken ct)
    {
        if (command.StoreId == null) return StoredSecrets.None;

        var store = await LoadAsync(command.StoreId.Value, ct).ConfigureAwait(false);

        if (store.Kind != command.Kind)
            throw new PackageStoreConfigurationInvalidException(command.Kind, [$"Package store {store.Id} is a {store.Kind} store, not a {command.Kind} store"]);

        return SelectReusable(provider, command.Properties, store);
    }
}
