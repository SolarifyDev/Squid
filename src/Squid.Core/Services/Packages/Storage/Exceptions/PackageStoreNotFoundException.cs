namespace Squid.Core.Services.Packages.Storage.Exceptions;

public sealed class PackageStoreNotFoundException(int storeId) : InvalidOperationException($"Package store {storeId} was not found")
{
    public int StoreId { get; } = storeId;
}
