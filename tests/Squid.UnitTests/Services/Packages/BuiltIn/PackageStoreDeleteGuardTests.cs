using System.Linq;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.UnitTests.Services.Packages.Storage.Support;

namespace Squid.UnitTests.Services.Packages.BuiltIn;

public class PackageStoreDeleteGuardTests
{
    private readonly FakePackageStoreDataProvider _table = new();

    private sealed class Holding(params int[] storeIds) : IPackageStoreUsage
    {
        public int Asked { get; private set; }

        public Task<bool> HoldsFilesAsync(int storeId, CancellationToken ct)
        {
            Asked++;

            return Task.FromResult(storeIds.Contains(storeId));
        }
    }

    private PackageStoreService Service(params IPackageStoreUsage[] usages) => new(_table, [], usages);

    [Fact]
    public async Task Delete_OfAStoreSomethingHoldsFilesIn_IsRefused_AndTheRowStays()
    {
        _table.Seed("Local", "FileSystem", isDefault: true);

        var other = _table.Seed("Other", "FileSystem");

        await Should.ThrowAsync<PackageStoreInUseException>(() => Service(new Holding(other.Id)).DeleteAsync(other.Id, CancellationToken.None));

        _table.Rows.Select(r => r.Id).ShouldContain(other.Id);
    }

    [Fact]
    public async Task Delete_AnyUsageHoldingFiles_IsEnough()
    {
        _table.Seed("Local", "FileSystem", isDefault: true);

        var other = _table.Seed("Other", "FileSystem");

        await Should.ThrowAsync<PackageStoreInUseException>(() => Service(new Holding(), new Holding(other.Id)).DeleteAsync(other.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_OfAStoreNothingHoldsFilesIn_Succeeds_AfterAskingEveryUsage()
    {
        _table.Seed("Local", "FileSystem", isDefault: true);

        var other = _table.Seed("Other", "FileSystem");
        var first = new Holding(999);
        var second = new Holding();

        await Service(first, second).DeleteAsync(other.Id, CancellationToken.None);

        _table.Rows.Select(r => r.Id).ShouldNotContain(other.Id);
        first.Asked.ShouldBe(1);
        second.Asked.ShouldBe(1);
    }

    [Fact]
    public async Task Delete_OfTheDefaultStore_IsStillTheDefaultRefusal_NotTheInUseOne()
    {
        var seeded = _table.Seed("Local", "FileSystem", isDefault: true);

        await Should.ThrowAsync<DefaultPackageStoreDeletionException>(() => Service(new Holding(seeded.Id)).DeleteAsync(seeded.Id, CancellationToken.None));
    }
}
