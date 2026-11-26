using Squid.Core.Persistence.Entities.PackageStorage;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Message.Commands.PackageStorage;
using Squid.Message.Models.PackageStorage;

namespace Squid.Core.Services.Packages.Storage;

public partial class PackageStoreService(IPackageStoreDataProvider dataProvider, IEnumerable<IPackageBlobStoreProvider> providers) : IPackageStoreService
{
    public async Task<List<PackageStoreDto>> ListAsync(CancellationToken ct)
    {
        var stores = await dataProvider.GetAllAsync(ct).ConfigureAwait(false);

        return stores.Select(ToDto).ToList();
    }

    public async Task<PackageStoreDto> GetAsync(int id, CancellationToken ct)
    {
        var store = await LoadAsync(id, ct).ConfigureAwait(false);

        return ToDto(store);
    }

    public async Task<PackageStoreDto> CreateAsync(CreatePackageStoreCommand command, CancellationToken ct)
    {
        var provider = ResolveProvider(command.Kind);
        var name = RequireName(command.Kind, command.Name);
        var configuration = PackageStoreSettingsCheck.PrepareConfiguration(provider, command.Properties, command.Secrets, StoredSecrets.None);

        await EnsureNameFreeAsync(name, null, ct).ConfigureAwait(false);

        var store = new PackageStore { Name = name, Kind = command.Kind, Properties = SerializeProperties(configuration.Properties), Secret = PackageStoreConfigurationMapper.SerializeSettings(configuration.Secrets) };

        await dataProvider.AddAsync(store, ct).ConfigureAwait(false);

        return await GetAsync(store.Id, ct).ConfigureAwait(false);
    }

    public async Task<PackageStoreDto> UpdateAsync(UpdatePackageStoreCommand command, CancellationToken ct)
    {
        var store = await LoadAsync(command.Id, ct).ConfigureAwait(false);
        var provider = ResolveProvider(store.Kind);
        var name = RequireName(store.Kind, command.Name);
        var configuration = PackageStoreSettingsCheck.PrepareConfiguration(provider, command.Properties, command.Secrets, SelectReusable(provider, command.Properties, store));

        await EnsureNameFreeAsync(name, store.Id, ct).ConfigureAwait(false);

        var settingsChanged = !SameSettings(configuration.Properties, PackageStoreConfigurationMapper.ParseSettingsLeniently(store.Properties)) || !SameSettings(configuration.Secrets, PackageStoreConfigurationMapper.ParseSettingsLeniently(store.Secret));

        store.Name = name;
        store.Properties = SerializeProperties(configuration.Properties);
        store.Secret = PackageStoreConfigurationMapper.SerializeSettings(configuration.Secrets);

        await dataProvider.UpdateAsync(store, settingsChanged, ct).ConfigureAwait(false);

        return await GetAsync(store.Id, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var store = await LoadAsync(id, ct).ConfigureAwait(false);

        if (store.IsDefault)
            throw new DefaultPackageStoreDeletionException(id);

        var deleted = await dataProvider.DeleteIfNotDefaultAsync(id, ct).ConfigureAwait(false);

        if (deleted) return;

        // Not deleted although it existed and was not the default a moment ago: it was removed, or made the default, in between.
        await LoadAsync(id, ct).ConfigureAwait(false);

        throw new DefaultPackageStoreDeletionException(id);
    }

    public async Task<PackageStoreDto> SetDefaultAsync(int id, CancellationToken ct)
    {
        var changed = await dataProvider.SetDefaultAsync(id, ct).ConfigureAwait(false);

        if (!changed)
            throw new PackageStoreNotFoundException(id);

        return await GetAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<PackageBlobStoreTestResult> TestAsync(int id, CancellationToken ct)
    {
        var store = await LoadAsync(id, ct).ConfigureAwait(false);
        var provider = ResolveProvider(store.Kind);

        var result = await TestSavedAsync(provider, store, ct).ConfigureAwait(false);

        result.Warnings = PackageStoreSettingsCheck.BuildWarnings(provider, ReadConfiguration(store, new List<string>()));

        await dataProvider.RecordTestResultAsync(id, result.Success, result.Message, ct).ConfigureAwait(false);

        return result;
    }

    public async Task<PackageBlobStoreTestResult> TestSettingsAsync(TestPackageStoreSettingsCommand command, CancellationToken ct)
    {
        var provider = ResolveProvider(command.Kind);
        var stored = await LoadStoredSecretsAsync(provider, command, ct).ConfigureAwait(false);

        return await PackageStoreSettingsCheck.TestAsync(provider, command.Properties, command.Secrets, stored, ct).ConfigureAwait(false);
    }

    public List<PackageStoreProviderDescriptor> DescribeProviders()
    {
        return providers.Select(p => p.Describe()).OrderBy(d => d.DisplayName, StringComparer.Ordinal).ToList();
    }

    private async Task<PackageStore> LoadAsync(int id, CancellationToken ct)
    {
        var store = await dataProvider.GetByIdAsync(id, ct).ConfigureAwait(false);

        return store ?? throw new PackageStoreNotFoundException(id);
    }

    private IPackageBlobStoreProvider ResolveProvider(string kind)
    {
        return PackageStoreSettingsCheck.ResolveProvider(providers, kind);
    }

    // A saved row whose settings cannot be read is a failed test, not an error: the operator opens it to fix it.
    private static async Task<PackageBlobStoreTestResult> TestSavedAsync(IPackageBlobStoreProvider provider, PackageStore store, CancellationToken ct)
    {
        try
        {
            return await provider.TestAsync(PackageStoreConfigurationMapper.ToConfiguration(store), ct).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return new PackageBlobStoreTestResult { Success = false, Message = $"The saved settings are invalid: {ex.Message}" };
        }
    }

    private static bool SameSettings(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && other == pair.Value);
    }
}
