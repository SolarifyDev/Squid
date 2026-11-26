namespace Squid.Core.Services.Packages.Storage.Exceptions;

public sealed class PackageStoreInUseException(int storeId) : InvalidOperationException($"Package store {storeId} still holds packages and cannot be deleted; its packages would be unreachable")
{
    public int StoreId { get; } = storeId;
}
