using Squid.Core.Persistence.Entities.PackageStorage;
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

    // A store that still holds files is not deleted: the records pointing at them would stay and every read of them would fail.
    private async Task EnsureNothingHoldsFilesAsync(int id, CancellationToken ct)
    {
        if (await HoldsFilesAsync(id, ct).ConfigureAwait(false))
            throw new PackageStoreInUseException(id);
    }

    // Packages pin (store, blob key) and no blob is moved, so a store that holds files keeps the settings that say where they live.
    private async Task EnsureLocationUnchangedWhileHoldingFilesAsync(PackageStore store, IPackageBlobStoreProvider provider, IReadOnlyDictionary<string, string> newProperties, CancellationToken ct)
    {
        var moved = ChangedLocationKeys(provider.Describe(), newProperties, PackageStoreConfigurationMapper.ParseSettingsLeniently(store.Properties));

        if (moved.Count == 0) return;

        if (await HoldsFilesAsync(store.Id, ct).ConfigureAwait(false))
            throw new PackageStoreLocationChangeException(store.Id, moved);
    }

    private async Task<bool> HoldsFilesAsync(int id, CancellationToken ct)
    {
        foreach (var usage in usages)
        {
            if (await usage.HoldsFilesAsync(id, ct).ConfigureAwait(false)) return true;
        }

        return false;
    }

    /// <summary>The keys of the fields the descriptor marks as the location whose value differs; an absent value counts as empty.</summary>
    public static IReadOnlyList<string> ChangedLocationKeys(PackageStoreProviderDescriptor descriptor, IReadOnlyDictionary<string, string> newProperties, IReadOnlyDictionary<string, string> storedProperties)
    {
        return descriptor.Fields.Where(f => f.IsLocation && ValueOf(newProperties, f.Key) != ValueOf(storedProperties, f.Key)).Select(f => f.Key).ToList();
    }

    private static string ValueOf(IReadOnlyDictionary<string, string> properties, string key)
    {
        return properties != null && properties.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
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
