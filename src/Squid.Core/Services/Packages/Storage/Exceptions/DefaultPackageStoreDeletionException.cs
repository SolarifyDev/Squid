namespace Squid.Core.Services.Packages.Storage.Exceptions;

public sealed class DefaultPackageStoreDeletionException(int storeId) : InvalidOperationException($"Package store {storeId} is the default store and cannot be deleted; make another store the default first")
{
    public int StoreId { get; } = storeId;
}
