using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.PackageStorage;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Message.Commands.PackageStorage;
using Squid.Message.Models.PackageStorage;

namespace Squid.IntegrationTests.Services.Packages;

/// <summary>
/// Races and edge cases of the package store writes, against real Postgres: an edit must never undo a concurrent set-default or test result, losers of
/// a race get a domain exception (never a raw database error), a row whose settings are broken cannot break the list, and a stored secret never travels
/// to a destination the caller just typed in. Every test inserts its own rows (the base class truncates every table after each test).
/// </summary>
public class PackageStoreRaceIntegrationTests : TestBase
{
    private const string Token = "race-token-31d4-PLAINTEXT-VALUE";
    private const string SecretKind = "SecretProbe";

    public PackageStoreRaceIntegrationTests() : base("PackageStoreRace", "squid_it_package_store_race", builder => builder.RegisterType<PackageStoreIntegrationTests.SecretProbeProvider>().As<IPackageBlobStoreProvider>().SingleInstance())
    {
    }

    private static CreatePackageStoreCommand NewSecretStore(string name) => new()
    {
        Name = name, Kind = SecretKind,
        Properties = new Dictionary<string, string> { ["Endpoint"] = "https://example" },
        Secrets = new Dictionary<string, string> { ["Token"] = Token }
    };

    private Task<PackageStoreDto> CreateAsync(string name) => Run<IPackageStoreService, PackageStoreDto>(service => service.CreateAsync(NewSecretStore(name), CancellationToken.None));

    private Task<PackageStore> RawRowAsync(int id) => Run<IRepository, PackageStore>(repository => repository.QueryNoTracking<PackageStore>(s => s.Id == id).SingleAsync());

    private Task<List<int>> DefaultIdsAsync() => Run<IRepository, List<int>>(repository => repository.QueryNoTracking<PackageStore>(s => s.IsDefault).Select(s => s.Id).ToListAsync());

    private async Task<int> InsertAsync(string name, bool isDefault, string kind = "FileSystem", string properties = "{}")
    {
        var id = 0;

        await Run<IPackageStoreDataProvider>(async provider =>
        {
            var store = new PackageStore { Name = name, Kind = kind, Properties = properties, IsDefault = isDefault };

            await provider.AddAsync(store, CancellationToken.None);
            id = store.Id;
        });

        return id;
    }

    [Fact]
    public async Task Update_ReadBeforeASetDefault_DoesNotUndoTheNewDefault()
    {
        var a = await InsertAsync("A", isDefault: true);
        var b = await InsertAsync("B", isDefault: false);

        var stale = await Run<IPackageStoreDataProvider, PackageStore>(provider => provider.GetByIdAsync(b, CancellationToken.None));

        await Run<IPackageStoreService>(service => service.SetDefaultAsync(b, CancellationToken.None));

        stale.Name = "B renamed";

        await Run<IPackageStoreDataProvider>(provider => provider.UpdateAsync(stale, resetTestResult: false, CancellationToken.None));

        (await DefaultIdsAsync()).ShouldBe([b], "the edit read B before it became the default and must not write IsDefault = false back");
        (await RawRowAsync(b)).Name.ShouldBe("B renamed");
        a.ShouldNotBe(b);
    }

    [Fact]
    public async Task Update_OfTheOldDefault_ReadBeforeASetDefault_DoesNotMakeItDefaultAgain()
    {
        var a = await InsertAsync("A", isDefault: true);
        var b = await InsertAsync("B", isDefault: false);

        var stale = await Run<IPackageStoreDataProvider, PackageStore>(provider => provider.GetByIdAsync(a, CancellationToken.None));

        await Run<IPackageStoreService>(service => service.SetDefaultAsync(b, CancellationToken.None));

        stale.Name = "A renamed";

        await Run<IPackageStoreDataProvider>(provider => provider.UpdateAsync(stale, resetTestResult: false, CancellationToken.None));

        (await DefaultIdsAsync()).ShouldBe([b], "writing the stale IsDefault = true back would trip the unique index (a 500) or leave two defaults");
        (await RawRowAsync(a)).Name.ShouldBe("A renamed");
    }

    [Fact]
    public async Task Update_ReadBeforeATest_KeepsTheTestResult_UnlessTheEditResetsIt()
    {
        var id = await InsertAsync("A", isDefault: true);
        var stale = await Run<IPackageStoreDataProvider, PackageStore>(provider => provider.GetByIdAsync(id, CancellationToken.None));

        await Run<IPackageStoreDataProvider>(provider => provider.RecordTestResultAsync(id, true, "worked", CancellationToken.None));

        stale.Name = "A renamed";
        await Run<IPackageStoreDataProvider>(provider => provider.UpdateAsync(stale, resetTestResult: false, CancellationToken.None));

        var kept = await RawRowAsync(id);

        kept.LastTestSucceeded.ShouldBe(true, "the edit never read the test result, so it must not overwrite it");
        kept.LastTestMessage.ShouldBe("worked");

        await Run<IPackageStoreDataProvider>(provider => provider.UpdateAsync(stale, resetTestResult: true, CancellationToken.None));

        var reset = await RawRowAsync(id);

        reset.LastTestSucceeded.ShouldBeNull();
        reset.LastTestedDate.ShouldBeNull();
        reset.LastTestMessage.ShouldBeNull();
    }

    [Fact]
    public async Task Update_ThroughTheService_ClearsTheTestResultWhenSettingsChange_ButNotForARename()
    {
        var dto = await CreateAsync("Remote");
        await Run<IPackageStoreService>(service => service.TestAsync(dto.Id, CancellationToken.None));

        await Run<IPackageStoreService>(service => service.UpdateAsync(new UpdatePackageStoreCommand { Id = dto.Id, Name = "Renamed", Properties = new Dictionary<string, string> { ["Endpoint"] = "https://example" } }, CancellationToken.None));

        (await RawRowAsync(dto.Id)).LastTestSucceeded.ShouldBe(true);

        await Run<IPackageStoreService>(service => service.UpdateAsync(new UpdatePackageStoreCommand { Id = dto.Id, Name = "Renamed", Properties = new Dictionary<string, string> { ["Endpoint"] = "https://example" }, Secrets = new Dictionary<string, string> { ["Token"] = "rotated" } }, CancellationToken.None));

        (await RawRowAsync(dto.Id)).LastTestSucceeded.ShouldBeNull();
    }

    [Fact]
    public async Task Update_OfAStoreDeletedMeanwhile_ThrowsNotFound()
    {
        await InsertAsync("Default", isDefault: true);
        var id = await InsertAsync("Doomed", isDefault: false);
        var stale = await Run<IPackageStoreDataProvider, PackageStore>(provider => provider.GetByIdAsync(id, CancellationToken.None));

        await Run<IPackageStoreService>(service => service.DeleteAsync(id, CancellationToken.None));

        await Should.ThrowAsync<PackageStoreNotFoundException>(() => Run<IPackageStoreDataProvider>(provider => provider.UpdateAsync(stale, false, CancellationToken.None)));
    }

    [Fact]
    public async Task ConcurrentCreates_OfTheSameName_LeaveOneRow_AndTheLosersGetTheNameConflict()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => TryAsync(() => InsertAsync("Same", isDefault: false))));

        results.Count(r => r == null).ShouldBe(1, "exactly one creator wins");
        results.Where(r => r != null).ShouldAllBe(e => e is PackageStoreNameConflictException, "a loser must see the name conflict, not a unique-index error");
        (await Run<IRepository, int>(repository => repository.QueryNoTracking<PackageStore>().CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task Names_DifferingOnlyByCase_Conflict_InTheDatabaseToo()
    {
        await InsertAsync("Remote", isDefault: false);

        await Should.ThrowAsync<PackageStoreNameConflictException>(() => InsertAsync("REMOTE", isDefault: false));
        await Should.ThrowAsync<PackageStoreNameConflictException>(() => CreateAsync("remote"));
    }

    [Fact]
    public async Task Rename_OntoAnotherStoresName_ThrowsTheNameConflict()
    {
        await InsertAsync("One", isDefault: false);
        var two = await InsertAsync("Two", isDefault: false);
        var stale = await Run<IPackageStoreDataProvider, PackageStore>(provider => provider.GetByIdAsync(two, CancellationToken.None));

        stale.Name = "ONE";

        await Should.ThrowAsync<PackageStoreNameConflictException>(() => Run<IPackageStoreDataProvider>(provider => provider.UpdateAsync(stale, false, CancellationToken.None)));
        (await RawRowAsync(two)).Name.ShouldBe("Two");
    }

    [Fact]
    public async Task ConcurrentSetDefaults_NeverFailWithADatabaseError_AndLeaveExactlyOneDefault()
    {
        var ids = new[] { await InsertAsync("A", true), await InsertAsync("B", false), await InsertAsync("C", false) };

        for (var round = 0; round < 15; round++)
        {
            var outcomes = await Task.WhenAll(ids.Select(id => TryAsync(() => Run<IPackageStoreDataProvider>(provider => provider.SetDefaultAsync(id, CancellationToken.None)))));

            outcomes.Where(o => o != null).ShouldAllBe(e => e is PackageStoreConcurrentChangeException, $"round {round}: a loser may only report a concurrent change");
            (await DefaultIdsAsync()).Count.ShouldBe(1, $"round {round}");
        }
    }

    [Fact]
    public async Task SetDefaultRacingADelete_ReportsADomainError_OrSucceeds_NeverADatabaseError()
    {
        for (var round = 0; round < 12; round++)
        {
            await InsertAsync($"Keep {round}", true);
            var target = await InsertAsync($"Target {round}", false);

            var outcomes = await Task.WhenAll(
                TryAsync(() => Run<IPackageStoreService>(service => service.SetDefaultAsync(target, CancellationToken.None))),
                TryAsync(() => Run<IPackageStoreService>(service => service.DeleteAsync(target, CancellationToken.None))));

            outcomes.Where(o => o != null).ShouldAllBe(e => e is PackageStoreNotFoundException || e is PackageStoreConcurrentChangeException || e is DefaultPackageStoreDeletionException, $"round {round}: {string.Join(", ", outcomes.Select(o => o?.GetType().Name))}");
            (await DefaultIdsAsync()).Count.ShouldBe(1, $"round {round}");

            await Run<IRepository>(repository => repository.ExecuteDeleteAsync<PackageStore>(s => s.Id > 0));
        }
    }

    [Fact]
    public async Task ListingAllStores_ThenSavingInTheSameScope_DoesNotWritePlaintextBack()
    {
        var dto = await CreateAsync("Remote");

        await Run<IPackageStoreDataProvider, IUnitOfWork>(async (provider, unitOfWork) =>
        {
            (await provider.GetAllAsync(CancellationToken.None)).Single().Secret.ShouldContain(Token);

            await unitOfWork.SaveChangesAsync();
        });

        var raw = (await RawRowAsync(dto.Id)).Secret;

        raw.ShouldStartWith("SQUID_ENCRYPTED_V2:");
        raw.ShouldNotContain(Token);
    }

    [Fact]
    public async Task ReadingTheDefaultStore_ThenSavingInTheSameScope_DoesNotWritePlaintextBack()
    {
        var dto = await CreateAsync("Remote");
        await Run<IPackageStoreService>(service => service.SetDefaultAsync(dto.Id, CancellationToken.None));

        await Run<IPackageStoreDataProvider, IUnitOfWork>(async (provider, unitOfWork) =>
        {
            (await provider.GetDefaultAsync(CancellationToken.None)).Secret.ShouldContain(Token);

            await unitOfWork.SaveChangesAsync();
        });

        var raw = (await RawRowAsync(dto.Id)).Secret;

        raw.ShouldStartWith("SQUID_ENCRYPTED_V2:");
        raw.ShouldNotContain(Token);
    }

    [Fact]
    public async Task ARowWithBrokenSettings_DoesNotBreakListGetOrTest_ForEveryone()
    {
        await InsertAsync("Healthy", true);
        var nul = await InsertAsync("NulRoot", false, properties: "{\"Root\":\"/srv/\\u0000bad\"}");
        var system = await InsertAsync("SystemRoot", false, properties: "{\"Root\":\"/etc\"}");
        var json = await InsertAsync("BrokenJson", false, properties: "{not json");

        var list = await Run<IPackageStoreService, List<PackageStoreDto>>(service => service.ListAsync(CancellationToken.None));

        list.Count.ShouldBe(4);
        list.Where(d => d.Name != "Healthy").ShouldAllBe(d => d.Warnings.Any(w => w.Contains("settings are invalid")));

        foreach (var id in new[] { nul, system, json })
        {
            await Should.NotThrowAsync(() => Run<IPackageStoreService>(service => service.GetAsync(id, CancellationToken.None)));
            (await Run<IPackageStoreService, PackageBlobStoreTestResult>(service => service.TestAsync(id, CancellationToken.None))).Success.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Create_WithAnInvalidRoot_IsRejected_AndNoRowIsWritten()
    {
        foreach (var root in new[] { "/srv/\0bad", "/", "/etc", "/var/lib", "relative", AppContext.BaseDirectory })
        {
            var command = new CreatePackageStoreCommand { Name = "Bad", Kind = "FileSystem", Properties = new Dictionary<string, string> { ["Root"] = root } };

            await Should.ThrowAsync<PackageStoreConfigurationInvalidException>(() => Run<IPackageStoreService>(service => service.CreateAsync(command, CancellationToken.None)), $"root {root}");
        }

        (await Run<IRepository, int>(repository => repository.QueryNoTracking<PackageStore>().CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task TestSettings_WithAChangedEndpoint_NeverUsesTheStoredToken()
    {
        var dto = await CreateAsync("Remote");

        await Should.ThrowAsync<PackageStoreConfigurationInvalidException>(() => Run<IPackageStoreService>(service => service.TestSettingsAsync(new TestPackageStoreSettingsCommand
        {
            Kind = SecretKind, StoreId = dto.Id, Properties = new Dictionary<string, string> { ["Endpoint"] = "https://attacker.example" }
        }, CancellationToken.None)));

        var unchanged = await Run<IPackageStoreService, PackageBlobStoreTestResult>(service => service.TestSettingsAsync(new TestPackageStoreSettingsCommand
        {
            Kind = SecretKind, StoreId = dto.Id, Properties = new Dictionary<string, string> { ["Endpoint"] = "https://example" }
        }, CancellationToken.None));

        unchanged.Message.ShouldBe($"token={Token}", "with the bound field unchanged the stored token is still used");
    }

    [Fact]
    public async Task Update_WithAChangedEndpoint_AndNoNewToken_IsRejected_AndTheStoredStoreIsUntouched()
    {
        var dto = await CreateAsync("Remote");
        var before = await RawRowAsync(dto.Id);

        await Should.ThrowAsync<PackageStoreConfigurationInvalidException>(() => Run<IPackageStoreService>(service => service.UpdateAsync(new UpdatePackageStoreCommand
        {
            Id = dto.Id, Name = "Remote", Properties = new Dictionary<string, string> { ["Endpoint"] = "https://attacker.example" }
        }, CancellationToken.None)));

        var after = await RawRowAsync(dto.Id);

        after.Properties.ShouldBe(before.Properties);
        after.Secret.ShouldBe(before.Secret);
    }

    private static async Task<Exception> TryAsync(Func<Task> action)
    {
        try
        {
            await action();

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Task<Exception> TryAsync<T>(Func<Task<T>> action) => TryAsync((Func<Task>)(() => action()));
}
