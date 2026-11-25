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
        var secrets = command.Secrets ?? new Dictionary<string, string>();

        PackageStoreSettingsCheck.EnsureValid(provider, command.Properties, secrets, new Dictionary<string, string>());

        await EnsureNameFreeAsync(name, null, ct).ConfigureAwait(false);

        var store = new PackageStore { Name = name, Kind = command.Kind, Properties = SerializeProperties(command.Properties), Secret = PackageStoreConfigurationMapper.SerializeSettings(secrets) };

        await dataProvider.AddAsync(store, ct).ConfigureAwait(false);

        return await GetAsync(store.Id, ct).ConfigureAwait(false);
    }

    public async Task<PackageStoreDto> UpdateAsync(UpdatePackageStoreCommand command, CancellationToken ct)
    {
        var store = await LoadAsync(command.Id, ct).ConfigureAwait(false);
        var provider = ResolveProvider(store.Kind);
        var name = RequireName(store.Kind, command.Name);
        var stored = PackageStoreConfigurationMapper.ParseSettings(store.Secret);
        var secrets = command.Secrets ?? new Dictionary<string, string>();

        PackageStoreSettingsCheck.EnsureValid(provider, command.Properties, secrets, stored);

        await EnsureNameFreeAsync(name, store.Id, ct).ConfigureAwait(false);

        store.Name = name;
        store.Properties = SerializeProperties(command.Properties);
        store.Secret = PackageStoreConfigurationMapper.SerializeSettings(PackageStoreSettings.MergeSecrets(stored, secrets));

        await dataProvider.UpdateAsync(store, ct).ConfigureAwait(false);

        return await GetAsync(store.Id, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var store = await LoadAsync(id, ct).ConfigureAwait(false);

        if (store.IsDefault)
            throw new DefaultPackageStoreDeletionException(id);

        var deleted = await dataProvider.DeleteIfNotDefaultAsync(id, ct).ConfigureAwait(false);

        // Not deleted although it existed and was not the default a moment ago: it was made the default in between.
        if (!deleted)
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

        var result = await provider.TestAsync(PackageStoreConfigurationMapper.ToConfiguration(store), ct).ConfigureAwait(false);

        await dataProvider.RecordTestResultAsync(id, result.Success, result.Message, ct).ConfigureAwait(false);

        return result;
    }

    public async Task<PackageBlobStoreTestResult> TestSettingsAsync(TestPackageStoreSettingsCommand command, CancellationToken ct)
    {
        var provider = ResolveProvider(command.Kind);
        var stored = await LoadStoredSecretsAsync(command, ct).ConfigureAwait(false);

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
}
