using System.Linq;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Message.Commands.PackageStorage;
using Squid.Message.Enums.PackageStorage;
using Squid.Message.Models.PackageStorage;
using Squid.UnitTests.Services.Packages.Storage.Support;

namespace Squid.UnitTests.Services.Packages.Storage;

public sealed class PackageStoreLocationGuardTests
{
    private const string Kind = "Bucketed";

    private readonly FakePackageStoreDataProvider _table = new();

    private sealed class Holding(params int[] storeIds) : IPackageStoreUsage
    {
        public Task<bool> HoldsFilesAsync(int storeId, CancellationToken ct) => Task.FromResult(storeIds.Contains(storeId));
    }

    // One location field, one plain field and one secret: enough to tell which setting moves data.
    private sealed class BucketedProvider : IPackageBlobStoreProvider
    {
        public string Kind => PackageStoreLocationGuardTests.Kind;

        public PackageStoreProviderDescriptor Describe() => new()
        {
            Kind = Kind, DisplayName = "Bucketed", Description = "test",
            Fields =
            [
                new PackageStoreFieldDescriptor { Key = "Bucket", Label = "Bucket", Kind = PackageStoreFieldKind.Text, Required = true, IsLocation = true },
                new PackageStoreFieldDescriptor { Key = "Timeout", Label = "Timeout", Kind = PackageStoreFieldKind.Text },
                new PackageStoreFieldDescriptor { Key = "Key", Label = "Key", Kind = PackageStoreFieldKind.Secret }
            ]
        };

        public IReadOnlyList<string> ValidateConfiguration(PackageBlobStoreConfiguration configuration) => [];

        public IPackageBlobStore Create(PackageBlobStoreConfiguration configuration) => throw new NotSupportedException();

        public Task<PackageBlobStoreTestResult> TestAsync(PackageBlobStoreConfiguration configuration, CancellationToken ct) => Task.FromResult(new PackageBlobStoreTestResult { Success = true, Message = "ok" });
    }

    private PackageStoreService Service(bool holdsFiles) => new(_table, [new BucketedProvider()], holdsFiles ? [new Holding(1)] : [new Holding()]);

    private int SeedStore() => _table.Seed("Remote", Kind, "{\"Bucket\":\"a\",\"Timeout\":\"5\"}", "{\"Key\":\"old\"}", isDefault: true).Id;

    private static UpdatePackageStoreCommand Update(string name = "Remote", string bucket = "a", string timeout = "5", string key = null)
    {
        return new UpdatePackageStoreCommand
        {
            Id = 1, Name = name,
            Properties = new Dictionary<string, string> { ["Bucket"] = bucket, ["Timeout"] = timeout },
            Secrets = key == null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Key"] = key }
        };
    }

    [Fact]
    public async Task Update_ChangingTheLocation_OfAStoreHoldingFiles_IsRefused_AndTheRowIsUntouched()
    {
        SeedStore();

        var ex = await Should.ThrowAsync<PackageStoreLocationChangeException>(() => Service(holdsFiles: true).UpdateAsync(Update(bucket: "b"), CancellationToken.None));

        ex.SettingKeys.ShouldBe(["Bucket"]);
        _table.Rows.Single().Properties.ShouldContain("\"a\"");
    }

    [Fact]
    public async Task Update_RenamingAStoreHoldingFiles_IsAllowed()
    {
        SeedStore();

        await Service(holdsFiles: true).UpdateAsync(Update(name: "Renamed"), CancellationToken.None);

        _table.Rows.Single().Name.ShouldBe("Renamed");
    }

    [Fact]
    public async Task Update_ChangingASettingThatDoesNotMoveData_OfAStoreHoldingFiles_IsAllowed()
    {
        SeedStore();

        await Service(holdsFiles: true).UpdateAsync(Update(timeout: "30"), CancellationToken.None);

        _table.Rows.Single().Properties.ShouldContain("30");
    }

    [Fact]
    public async Task Update_RotatingASecretForTheSameLocation_OfAStoreHoldingFiles_IsAllowed()
    {
        SeedStore();

        await Service(holdsFiles: true).UpdateAsync(Update(key: "rotated"), CancellationToken.None);

        _table.Rows.Single().Secret.ShouldContain("rotated");
    }

    [Fact]
    public async Task Update_ChangingTheLocation_OfAnEmptyStore_IsAllowed()
    {
        SeedStore();

        await Service(holdsFiles: false).UpdateAsync(Update(bucket: "b"), CancellationToken.None);

        _table.Rows.Single().Properties.ShouldContain("\"b\"");
    }

    [Theory]
    [InlineData("a", "a", false)]
    [InlineData("b", "a", true)]
    [InlineData("", null, false)]
    [InlineData("x", null, true)]
    public void ChangedLocationKeys_ComparesOnlyLocationFields_TreatingAbsentAsEmpty(string newValue, string storedValue, bool changed)
    {
        var descriptor = new BucketedProvider().Describe();
        var stored = storedValue == null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Bucket"] = storedValue };

        var keys = PackageStoreService.ChangedLocationKeys(descriptor, new Dictionary<string, string> { ["Bucket"] = newValue, ["Timeout"] = "9" }, stored);

        keys.Count.ShouldBe(changed ? 1 : 0);
    }
}
