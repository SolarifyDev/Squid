using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.PackageStorage;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Core.Services.Packages.Storage.Providers.FileSystem;
using Squid.Message.Commands.PackageStorage;
using Squid.Message.Enums.PackageStorage;
using Squid.Message.Models.PackageStorage;
using Squid.Message.Requests.PackageStorage;

namespace Squid.IntegrationTests.Services.Packages;

/// <summary>
/// Real Postgres + real EF + real at-rest protector. Every test inserts its own rows: the base class truncates every table (the seeded default
/// row included) after each test. The migration's own seed is covered in <see cref="PackageStoreMigrationIntegrationTests"/> on a fresh database.
/// </summary>
public class PackageStoreIntegrationTests : TestBase
{
    private const string Token = "pkg-token-9a7c-PLAINTEXT-VALUE";
    private const string SecretKind = "SecretProbe";

    public PackageStoreIntegrationTests() : base("PackageStore", "squid_it_package_store", builder => builder.RegisterType<SecretProbeProvider>().As<IPackageBlobStoreProvider>().SingleInstance())
    {
        ResetPackageStores();
    }

    // The migration seeds a default store, and the base class only truncates after a test: whichever test runs first in a fresh database would see that row.
    // Start every test from an empty table so no test depends on the seed or on the order the tests run in.
    private void ResetPackageStores()
    {
        Run<IRepository>(repository => repository.ExecuteDeleteAsync<PackageStore>(s => s.Id > 0)).GetAwaiter().GetResult();
    }

    private static CreatePackageStoreCommand NewSecretStore(string name = "Remote") => new()
    {
        Name = name, Kind = SecretKind,
        Properties = new Dictionary<string, string> { ["Endpoint"] = "https://example" },
        Secrets = new Dictionary<string, string> { ["Token"] = Token }
    };

    private Task<PackageStoreDto> CreateAsync(CreatePackageStoreCommand command) => Run<IPackageStoreService, PackageStoreDto>(service => service.CreateAsync(command, CancellationToken.None));

    private Task<PackageStore> RawRowAsync(int id) => Run<IRepository, PackageStore>(repository => repository.QueryNoTracking<PackageStore>(s => s.Id == id).SingleAsync());

    private async Task<int> InsertDefaultAsync(string name, string kind = "FileSystem", bool isDefault = true)
    {
        var id = 0;

        await Run<IPackageStoreDataProvider>(async provider =>
        {
            var store = new PackageStore { Name = name, Kind = kind, Properties = "{}", IsDefault = isDefault };

            await provider.AddAsync(store, CancellationToken.None);
            id = store.Id;
        });

        return id;
    }

    [Fact]
    public async Task Create_StoresTheSecretAsCiphertext_NeverPlaintext_AndTheDtoNeverCarriesIt()
    {
        var dto = await CreateAsync(NewSecretStore());

        var raw = await RawRowAsync(dto.Id);

        raw.Secret.ShouldStartWith("SQUID_ENCRYPTED_V2:", customMessage: "the persisted secret JSON must be a V2 envelope");
        raw.Secret.ShouldNotContain(Token);
        raw.Properties.ShouldNotContain(Token);

        JsonSerializer.Serialize(dto).ShouldNotContain(Token);
        dto.SecretHasValue["Token"].ShouldBeTrue();
        dto.SecretHasValue["Proxy"].ShouldBeFalse();

        var listed = await Run<IPackageStoreService, List<PackageStoreDto>>(service => service.ListAsync(CancellationToken.None));

        JsonSerializer.Serialize(listed).ShouldNotContain(Token);
    }

    [Fact]
    public async Task Registry_BuildsTheStoreWithTheDecryptedSecret()
    {
        var dto = await CreateAsync(NewSecretStore());

        var store = await Run<IPackageBlobStoreRegistry, IPackageBlobStore>(registry => registry.ResolveAsync(dto.Id, CancellationToken.None));

        ((SecretProbeProvider.ProbeStore)store).Configuration.Secret("Token").ShouldBe(Token);
        ((SecretProbeProvider.ProbeStore)store).Configuration.Property("Endpoint").ShouldBe("https://example");
    }

    [Fact]
    public async Task Update_WithoutSecrets_KeepsTheStoredSecret_AndDoesNotWritePlaintextBack()
    {
        var dto = await CreateAsync(NewSecretStore());
        var before = (await RawRowAsync(dto.Id)).Secret;

        await Run<IPackageStoreService>(service => service.UpdateAsync(new UpdatePackageStoreCommand { Id = dto.Id, Name = "Renamed", Properties = new Dictionary<string, string> { ["Endpoint"] = "https://example" } }, CancellationToken.None));

        var after = await RawRowAsync(dto.Id);

        after.Name.ShouldBe("Renamed");
        after.Secret.ShouldStartWith("SQUID_ENCRYPTED_V2:");
        after.Secret.ShouldNotContain(Token);
        (await DecryptedSecretAsync(dto.Id)).ShouldBe(Token, "the stored secret must survive an update that did not mention it");
        before.ShouldStartWith("SQUID_ENCRYPTED_V2:");
    }

    [Fact]
    public async Task Update_WithANewSecret_ReplacesIt()
    {
        var dto = await CreateAsync(NewSecretStore());

        await Run<IPackageStoreService>(service => service.UpdateAsync(new UpdatePackageStoreCommand { Id = dto.Id, Name = "Remote", Properties = new Dictionary<string, string> { ["Endpoint"] = "https://example" }, Secrets = new Dictionary<string, string> { ["Token"] = "rotated-token" } }, CancellationToken.None));

        (await DecryptedSecretAsync(dto.Id)).ShouldBe("rotated-token");
        (await RawRowAsync(dto.Id)).Secret.ShouldNotContain("rotated-token");
    }

    [Fact]
    public async Task ReadingAStoreThenAnUnrelatedSaveInTheSameScope_DoesNotFlushPlaintextBack()
    {
        var dto = await CreateAsync(NewSecretStore());

        await Run<IPackageStoreDataProvider, IPackageStoreService, IUnitOfWork>(async (provider, service, unitOfWork) =>
        {
            (await provider.GetByIdAsync(dto.Id, CancellationToken.None)).Secret.ShouldContain(Token);
            await service.GetAsync(dto.Id, CancellationToken.None);

            await unitOfWork.SaveChangesAsync();
        });

        var raw = await RawRowAsync(dto.Id);

        raw.Secret.ShouldStartWith("SQUID_ENCRYPTED_V2:");
        raw.Secret.ShouldNotContain(Token);
    }

    [Fact]
    public async Task Create_WithADuplicateName_IsRejected()
    {
        await CreateAsync(NewSecretStore("Same"));

        await Should.ThrowAsync<PackageStoreNameConflictException>(() => CreateAsync(NewSecretStore("Same")));
    }

    [Fact]
    public async Task TheDatabaseRefusesASecondDefaultRow()
    {
        await InsertDefaultAsync("First");

        var ex = await Should.ThrowAsync<DbUpdateException>(() => InsertDefaultAsync("Second"));

        ((PostgresException)ex.InnerException!).SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        ((PostgresException)ex.InnerException!).ConstraintName.ShouldBe("ux_package_store_single_default");
    }

    [Fact]
    public async Task TheDatabaseAllowsManyNonDefaultRows_ButNotTwoWithTheSameName()
    {
        await InsertDefaultAsync("A", isDefault: false);
        await InsertDefaultAsync("B", isDefault: false);

        await Should.ThrowAsync<PackageStoreNameConflictException>(() => InsertDefaultAsync("a", isDefault: false));
    }

    [Fact]
    public async Task SetDefault_MovesTheFlagInOneStep_LeavingExactlyOneDefault()
    {
        var a = await InsertDefaultAsync("A");
        var b = await InsertDefaultAsync("B", isDefault: false);

        await Run<IPackageStoreService>(service => service.SetDefaultAsync(b, CancellationToken.None));

        (await DefaultIdsAsync()).ShouldBe([b]);

        await Run<IPackageStoreService>(service => service.SetDefaultAsync(b, CancellationToken.None));

        (await DefaultIdsAsync()).ShouldBe([b], "setting the current default again changes nothing");
        a.ShouldNotBe(b);
    }

    [Fact]
    public async Task SetDefault_OfAMissingStore_ChangesNothing()
    {
        var a = await InsertDefaultAsync("A");

        await Should.ThrowAsync<PackageStoreNotFoundException>(() => Run<IPackageStoreService>(service => service.SetDefaultAsync(9999, CancellationToken.None)));

        (await DefaultIdsAsync()).ShouldBe([a], "the old default must not be cleared when the new one does not exist");
    }

    [Fact]
    public async Task SetDefault_RacingCallers_NeverLeaveTwoDefaultsOrNone()
    {
        var a = await InsertDefaultAsync("A");
        var b = await InsertDefaultAsync("B", isDefault: false);
        var c = await InsertDefaultAsync("C", isDefault: false);

        for (var round = 0; round < 15; round++)
        {
            await Task.WhenAll(TrySetDefaultAsync(b), TrySetDefaultAsync(c), TrySetDefaultAsync(a));

            (await DefaultIdsAsync()).Count.ShouldBe(1, $"round {round}: exactly one default must survive concurrent changes");
        }
    }

    [Fact]
    public async Task Delete_OfTheDefaultStore_IsRejected_AndTheRowStays()
    {
        var a = await InsertDefaultAsync("A");

        await Should.ThrowAsync<DefaultPackageStoreDeletionException>(() => Run<IPackageStoreService>(service => service.DeleteAsync(a, CancellationToken.None)));

        (await RawRowAsync(a)).IsDefault.ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_OfANonDefaultStore_RemovesIt()
    {
        await InsertDefaultAsync("A");
        var b = await InsertDefaultAsync("B", isDefault: false);

        await Run<IPackageStoreService>(service => service.DeleteAsync(b, CancellationToken.None));

        await Should.ThrowAsync<PackageStoreNotFoundException>(() => Run<IPackageStoreService>(service => service.GetAsync(b, CancellationToken.None)));
    }

    [Fact]
    public async Task Test_OnAFileSystemStore_WritesAndRemovesTheProbe_AndRecordsTheOutcome_WithoutTouchingTheSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"squid-it-pkgstore-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(root);

            var dto = await CreateAsync(new CreatePackageStoreCommand { Name = "Disk", Kind = "FileSystem", Properties = new Dictionary<string, string> { ["Root"] = root } });
            var modifiedBefore = (await RawRowAsync(dto.Id)).LastModifiedDate;

            var result = await Run<IPackageStoreService, PackageBlobStoreTestResult>(service => service.TestAsync(dto.Id, CancellationToken.None));

            result.Success.ShouldBeTrue(result.Message);
            Directory.Exists(root).ShouldBeTrue("the probe really wrote into the configured folder");
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ShouldBeEmpty("the probe file must be removed again");

            var raw = await RawRowAsync(dto.Id);

            raw.LastTestSucceeded.ShouldBe(true);
            raw.LastTestedDate.ShouldNotBeNull();
            raw.LastTestMessage.ShouldContain(root);
            raw.LastModifiedDate.ShouldBe(modifiedBefore, "recording a test result must not look like an edit of the store");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Test_OnAFolderThatCannotBeUsed_RecordsAFailure()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"squid-it-pkgstore-file-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blocker, "x");

        try
        {
            var dto = await CreateAsync(new CreatePackageStoreCommand { Name = "Broken", Kind = "FileSystem", Properties = new Dictionary<string, string> { ["Root"] = Path.Combine(blocker, "pkgs") } });

            var result = await Run<IPackageStoreService, PackageBlobStoreTestResult>(service => service.TestAsync(dto.Id, CancellationToken.None));

            result.Success.ShouldBeFalse();
            (await RawRowAsync(dto.Id)).LastTestSucceeded.ShouldBe(false);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task Seam_TheFileSystemProvider_IsRegisteredInTheContainer_AndTheRegistryBuildsItsStore()
    {
        await Run<IEnumerable<IPackageBlobStoreProvider>>(providers =>
        {
            providers.ShouldContain(p => p is FileSystemPackageBlobStoreProvider && p.Kind == "FileSystem", "the real provider must be discovered by the DI scan; no provider, no storage");

            return Task.CompletedTask;
        });

        var id = await InsertDefaultAsync("Local disk");

        (await Run<IPackageBlobStoreRegistry, IPackageBlobStore>(registry => registry.ResolveDefaultAsync(CancellationToken.None))).ShouldBeOfType<FileSystemPackageBlobStore>();
        (await Run<IPackageBlobStoreRegistry, IPackageBlobStore>(registry => registry.ResolveAsync(id, CancellationToken.None))).ShouldBeOfType<FileSystemPackageBlobStore>();
    }

    [Fact]
    public async Task Seam_TheMediatorPipelineReachesTheHandlers_AndAdministerSystemIsEnforced()
    {
        var providers = await Run<IMediator, ListPackageStoreProvidersResponse>(mediator => mediator.RequestAsync<ListPackageStoreProvidersRequest, ListPackageStoreProvidersResponse>(new ListPackageStoreProvidersRequest()));

        providers.Data.Select(p => p.Kind).ShouldContain("FileSystem");

        var created = await Run<IMediator, CreatePackageStoreResponse>(mediator => mediator.SendAsync<CreatePackageStoreCommand, CreatePackageStoreResponse>(NewSecretStore("Via mediator")));

        created.Data.Name.ShouldBe("Via mediator");

        await Should.ThrowAsync<PermissionDeniedException>(() => Run<IMediator, ListPackageStoresResponse>(
            mediator => mediator.RequestAsync<ListPackageStoresRequest, ListPackageStoresResponse>(new ListPackageStoresRequest()),
            builder => builder.RegisterInstance<ICurrentUser>(new UnprivilegedUser()).SingleInstance()));
    }

    [Fact]
    public async Task TestSettings_UsesTheStoredSecretWhenLeftEmpty_AndChangesNothingInTheDatabase()
    {
        var dto = await CreateAsync(NewSecretStore());
        var before = await RawRowAsync(dto.Id);

        var result = await Run<IPackageStoreService, PackageBlobStoreTestResult>(service => service.TestSettingsAsync(new TestPackageStoreSettingsCommand
        {
            Kind = SecretKind, StoreId = dto.Id,
            Properties = new Dictionary<string, string> { ["Endpoint"] = "https://example" },
            Secrets = new Dictionary<string, string> { ["Token"] = "" }
        }, CancellationToken.None));

        result.Message.ShouldBe($"token={Token}", "the stored (decrypted) secret fills the empty field");

        var after = await RawRowAsync(dto.Id);

        after.Properties.ShouldBe(before.Properties);
        after.Secret.ShouldBe(before.Secret);
        after.LastModifiedDate.ShouldBe(before.LastModifiedDate);
        after.LastTestedDate.ShouldBeNull();
        after.LastTestSucceeded.ShouldBeNull();
        after.LastTestMessage.ShouldBeNull();
    }

    [Fact]
    public async Task TestSettings_OfAnUnsavedFileSystemFolder_RunsTheRealProbe_AndCreatesNoStore()
    {
        var root = Path.Combine(Path.GetTempPath(), $"squid-it-unsaved-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(root);

            var result = await Run<IMediator, TestPackageStoreSettingsResponse>(mediator => mediator.SendAsync<TestPackageStoreSettingsCommand, TestPackageStoreSettingsResponse>(
                new TestPackageStoreSettingsCommand { Kind = "FileSystem", Properties = new Dictionary<string, string> { ["Root"] = root } }));

            result.Data.Success.ShouldBeTrue(result.Data.Message);
            Directory.Exists(root).ShouldBeTrue();
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ShouldBeEmpty();
            (await Run<IRepository, int>(repository => repository.QueryNoTracking<PackageStore>().CountAsync())).ShouldBe(0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TestSettings_WithInvalidSettings_ThrowsTheProblems()
    {
        var ex = await Should.ThrowAsync<PackageStoreConfigurationInvalidException>(() => Run<IPackageStoreService>(service => service.TestSettingsAsync(
            new TestPackageStoreSettingsCommand { Kind = SecretKind, Properties = new Dictionary<string, string>() }, CancellationToken.None)));

        ex.Problems.Count.ShouldBe(2);
    }

    private async Task TrySetDefaultAsync(int id)
    {
        try
        {
            await Run<IPackageStoreDataProvider>(provider => provider.SetDefaultAsync(id, CancellationToken.None));
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or PostgresException)
        {
            // Losing the race is allowed (unique violation or deadlock victim); leaving two defaults is not, and is asserted by the caller.
        }
    }

    private Task<List<int>> DefaultIdsAsync() => Run<IRepository, List<int>>(repository => repository.QueryNoTracking<PackageStore>(s => s.IsDefault).Select(s => s.Id).ToListAsync());

    private async Task<string> DecryptedSecretAsync(int id)
    {
        var store = await Run<IPackageStoreDataProvider, PackageStore>(provider => provider.GetByIdAsync(id, CancellationToken.None));

        return PackageStoreConfigurationMapper.ParseSettings(store.Secret)["Token"];
    }

    private sealed class UnprivilegedUser : ICurrentUser
    {
        public int? Id => 424242;
        public string Name => "nobody";
        public bool IsInternal => false;
    }

    /// <summary>A remote-style kind with a secret field, registered next to the real providers so secret storage is exercised end to end.</summary>
    public sealed class SecretProbeProvider : IPackageBlobStoreProvider
    {
        public string Kind => SecretKind;

        public PackageStoreProviderDescriptor Describe() => new()
        {
            Kind = Kind, DisplayName = "Secret probe", Description = "test",
            Fields =
            [
                new PackageStoreFieldDescriptor { Key = "Endpoint", Label = "Endpoint", Kind = PackageStoreFieldKind.Text, Required = true },
                new PackageStoreFieldDescriptor { Key = "Token", Label = "Token", Kind = PackageStoreFieldKind.Secret, Required = true, BoundTo = ["Endpoint"] },
                new PackageStoreFieldDescriptor { Key = "Proxy", Label = "Proxy", Kind = PackageStoreFieldKind.Secret }
            ]
        };

        public IReadOnlyList<string> ValidateConfiguration(PackageBlobStoreConfiguration configuration) => [];

        public IPackageBlobStore Create(PackageBlobStoreConfiguration configuration) => new ProbeStore(configuration);

        public Task<PackageBlobStoreTestResult> TestAsync(PackageBlobStoreConfiguration configuration, CancellationToken ct) => Task.FromResult(new PackageBlobStoreTestResult { Success = true, Message = $"token={configuration.Secret("Token")}" });

        public sealed class ProbeStore(PackageBlobStoreConfiguration configuration) : IPackageBlobStore
        {
            public PackageBlobStoreConfiguration Configuration { get; } = configuration;
            public string Kind => SecretKind;
            public Task PutAsync(string key, Stream content, CancellationToken ct) => Task.CompletedTask;
            public Task<Stream> OpenReadAsync(string key, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream());
            public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(false);
            public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;

            public async IAsyncEnumerable<string> ListKeysAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                await Task.CompletedTask;
                yield break;
            }
        }
    }
}
