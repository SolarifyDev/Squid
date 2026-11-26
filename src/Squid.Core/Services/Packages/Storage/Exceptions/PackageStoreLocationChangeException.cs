namespace Squid.Core.Services.Packages.Storage.Exceptions;

public sealed class PackageStoreLocationChangeException(int storeId, IReadOnlyList<string> settingKeys) : InvalidOperationException($"Package store {storeId} still holds packages, so its location setting(s) {string.Join(", ", settingKeys)} cannot be changed; the packages are not moved and would become unreachable. Create a new store for the new location instead")
{
    public int StoreId { get; } = storeId;

    public IReadOnlyList<string> SettingKeys { get; } = settingKeys;
}
