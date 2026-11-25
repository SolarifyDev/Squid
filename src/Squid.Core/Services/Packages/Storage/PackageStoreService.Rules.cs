using Squid.Core.Services.Packages.Storage.Exceptions;

using Squid.Message.Commands.PackageStorage;

namespace Squid.Core.Services.Packages.Storage;

public partial class PackageStoreService
{
    private static string RequireName(string kind, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new PackageStoreConfigurationInvalidException(kind, ["\"Name\" is required"]);

        return name.Trim();
    }

    private static void EnsureSettingsValid(IPackageBlobStoreProvider provider, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> secrets, IReadOnlyDictionary<string, string> storedSecrets)
    {
        var problems = PackageStoreSettings.Validate(provider.Describe(), properties ?? new Dictionary<string, string>(), secrets ?? new Dictionary<string, string>(), storedSecrets);

        if (problems.Count > 0)
            throw new PackageStoreConfigurationInvalidException(provider.Kind, problems);
    }

    private async Task EnsureNameFreeAsync(string name, int? exceptId, CancellationToken ct)
    {
        if (await dataProvider.NameExistsAsync(name, exceptId, ct).ConfigureAwait(false))
            throw new PackageStoreNameConflictException(name);
    }

    private static string SerializeProperties(IReadOnlyDictionary<string, string> properties)
    {
        return PackageStoreConfigurationMapper.SerializeSettings(properties ?? new Dictionary<string, string>()) ?? "{}";
    }

    private async Task<Dictionary<string, string>> LoadStoredSecretsAsync(TestPackageStoreSettingsCommand command, CancellationToken ct)
    {
        if (command.StoreId == null) return new Dictionary<string, string>();

        var store = await LoadAsync(command.StoreId.Value, ct).ConfigureAwait(false);

        if (store.Kind != command.Kind)
            throw new PackageStoreConfigurationInvalidException(command.Kind, [$"Package store {store.Id} is a {store.Kind} store, not a {command.Kind} store"]);

        return PackageStoreConfigurationMapper.ParseSettings(store.Secret);
    }
}
