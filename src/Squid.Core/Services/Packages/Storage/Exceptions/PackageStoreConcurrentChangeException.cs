namespace Squid.Core.Services.Packages.Storage.Exceptions;

/// <summary>Another change to the package stores won a race with this one (two callers made a store the default at once, or the store was removed meanwhile). Retry.</summary>
public sealed class PackageStoreConcurrentChangeException(int storeId) : InvalidOperationException($"Package store {storeId} was changed by someone else at the same time; try again")
{
    public int StoreId { get; } = storeId;
}
