using System.IO;
using System.Linq;
using System.Text.Json;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Core.Services.Packages.Storage.Providers.FileSystem;
using Squid.Core.Settings.Setup;
using Squid.Message.Commands.PackageStorage;
using Squid.UnitTests.Services.Packages.Storage.Support;

namespace Squid.UnitTests.Services.Packages.Storage;

public sealed class PackageStoreServiceTests
{
    private const string PlaintextToken = "tok-4f1c-PLAINTEXT-VALUE";

    private readonly FakePackageStoreDataProvider _table = new();
    private readonly FakeSecretBlobStoreProvider _fake = new();
    private readonly PackageStoreService _service;

    public PackageStoreServiceTests()
    {
        _service = new PackageStoreService(_table, [_fake, new FileSystemPackageBlobStoreProvider(new DataDirectorySetting(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()))], []);
    }

    private static CreatePackageStoreCommand NewFake(string name = "Remote", string token = PlaintextToken)
    {
        return new CreatePackageStoreCommand
        {
            Name = name, Kind = FakeSecretBlobStoreProvider.FakeKind,
            Properties = new Dictionary<string, string> { ["Endpoint"] = "https://x" },
            Secrets = new Dictionary<string, string> { ["Token"] = token }
        };
    }

    [Fact]
    public async Task Create_StoresPropertiesAndSecretsSeparately_AndTheDtoCarriesNoSecretValue()
    {
        var dto = await _service.CreateAsync(NewFake(), CancellationToken.None);

        var row = _table.Rows.Single();

        JsonSerializer.Deserialize<Dictionary<string, string>>(row.Properties).ShouldBe(new Dictionary<string, string> { ["Endpoint"] = "https://x" });
        JsonSerializer.Deserialize<Dictionary<string, string>>(row.Secret).ShouldBe(new Dictionary<string, string> { ["Token"] = PlaintextToken });
        row.Properties.ShouldNotContain(PlaintextToken);

        dto.Properties.ShouldBe(new Dictionary<string, string> { ["Endpoint"] = "https://x" });
        dto.SecretHasValue.ShouldBe(new Dictionary<string, bool> { ["Token"] = true, ["Proxy"] = false });
        JsonSerializer.Serialize(dto).ShouldNotContain(PlaintextToken);
        dto.IsDefault.ShouldBeFalse("a new store never takes over as the default");
    }

    [Fact]
    public async Task Update_WithoutSecrets_KeepsTheStoredSecret()
    {
        var created = await _service.CreateAsync(NewFake(), CancellationToken.None);

        await _service.UpdateAsync(new UpdatePackageStoreCommand { Id = created.Id, Name = "Renamed", Properties = new Dictionary<string, string> { ["Endpoint"] = "https://y" } }, CancellationToken.None);

        var row = _table.Rows.Single();

        row.Name.ShouldBe("Renamed");
        JsonSerializer.Deserialize<Dictionary<string, string>>(row.Properties)["Endpoint"].ShouldBe("https://y");
        JsonSerializer.Deserialize<Dictionary<string, string>>(row.Secret)["Token"].ShouldBe(PlaintextToken);
    }

    [Fact]
    public async Task Update_WithNullOrEmptySecret_KeepsIt_AndAProvidedValueReplacesIt()
    {
        var created = await _service.CreateAsync(NewFake(), CancellationToken.None);
        var props = new Dictionary<string, string> { ["Endpoint"] = "https://x" };

        await _service.UpdateAsync(new UpdatePackageStoreCommand { Id = created.Id, Name = "Remote", Properties = props, Secrets = new Dictionary<string, string> { ["Token"] = null } }, CancellationToken.None);
        await _service.UpdateAsync(new UpdatePackageStoreCommand { Id = created.Id, Name = "Remote", Properties = props, Secrets = new Dictionary<string, string> { ["Token"] = "" } }, CancellationToken.None);

        JsonSerializer.Deserialize<Dictionary<string, string>>(_table.Rows.Single().Secret)["Token"].ShouldBe(PlaintextToken);

        await _service.UpdateAsync(new UpdatePackageStoreCommand { Id = created.Id, Name = "Remote", Properties = props, Secrets = new Dictionary<string, string> { ["Token"] = "rotated", ["Proxy"] = "p" } }, CancellationToken.None);

        JsonSerializer.Deserialize<Dictionary<string, string>>(_table.Rows.Single().Secret).ShouldBe(new Dictionary<string, string> { ["Token"] = "rotated", ["Proxy"] = "p" });
    }

    [Fact]
    public async Task Update_NeverChangesTheKind_AndKeepsTheDefaultFlag()
    {
        var seeded = _table.Seed("Local disk", FileSystemPackageBlobStore.StoreKind, isDefault: true);

        var dto = await _service.UpdateAsync(new UpdatePackageStoreCommand { Id = seeded.Id, Name = "Local disk", Properties = new Dictionary<string, string> { ["Root"] = "/srv/pk" } }, CancellationToken.None);

        dto.Kind.ShouldBe(FileSystemPackageBlobStore.StoreKind);
        dto.IsDefault.ShouldBeTrue();
        dto.Properties["Root"].ShouldBe("/srv/pk");
    }

    [Fact]
    public async Task Create_WithAnInvalidConfiguration_IsRejectedAndNothingIsSaved()
    {
        var command = NewFake(token: null);
        command.Properties["Surprise"] = "x";

        var ex = await Should.ThrowAsync<PackageStoreConfigurationInvalidException>(() => _service.CreateAsync(command, CancellationToken.None));

        ex.Problems.Count.ShouldBe(2);
        _table.Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_WithAnUnknownKind_ThrowsProviderNotFound()
    {
        var command = NewFake();
        command.Kind = "Quantum";

        await Should.ThrowAsync<PackageStoreProviderNotFoundException>(() => _service.CreateAsync(command, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Create_WithoutAName_IsRejected(string name)
    {
        await Should.ThrowAsync<PackageStoreConfigurationInvalidException>(() => _service.CreateAsync(NewFake(name: name), CancellationToken.None));
    }

    [Fact]
    public async Task Create_WithADuplicateName_ThrowsConflict_AndRenamingOntoAnotherStoreDoes()
    {
        await _service.CreateAsync(NewFake("One"), CancellationToken.None);

        await Should.ThrowAsync<PackageStoreNameConflictException>(() => _service.CreateAsync(NewFake("One"), CancellationToken.None));

        var two = await _service.CreateAsync(NewFake("Two"), CancellationToken.None);
        var rename = new UpdatePackageStoreCommand { Id = two.Id, Name = "One", Properties = new Dictionary<string, string> { ["Endpoint"] = "https://x" } };

        await Should.ThrowAsync<PackageStoreNameConflictException>(() => _service.UpdateAsync(rename, CancellationToken.None));

        rename.Name = "Two";
        await Should.NotThrowAsync(() => _service.UpdateAsync(rename, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_OfTheDefaultStore_IsRejected_AndTheRowStays()
    {
        var seeded = _table.Seed("Local disk", FileSystemPackageBlobStore.StoreKind, isDefault: true);

        await Should.ThrowAsync<DefaultPackageStoreDeletionException>(() => _service.DeleteAsync(seeded.Id, CancellationToken.None));

        _table.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Delete_OfANonDefaultStore_RemovesIt_AndOfAMissingOneThrowsNotFound()
    {
        _table.Seed("Local disk", FileSystemPackageBlobStore.StoreKind, isDefault: true);
        var other = await _service.CreateAsync(NewFake(), CancellationToken.None);

        await _service.DeleteAsync(other.Id, CancellationToken.None);

        _table.Rows.Select(r => r.Name).ShouldBe(["Local disk"]);
        await Should.ThrowAsync<PackageStoreNotFoundException>(() => _service.DeleteAsync(other.Id, CancellationToken.None));
    }

    [Fact]
    public async Task SetDefault_MovesTheFlag_AndOfAMissingStoreThrowsNotFound()
    {
        var a = _table.Seed("A", FileSystemPackageBlobStore.StoreKind, isDefault: true);
        var b = _table.Seed("B", FileSystemPackageBlobStore.StoreKind);

        var dto = await _service.SetDefaultAsync(b.Id, CancellationToken.None);

        dto.IsDefault.ShouldBeTrue();
        _table.Rows.Single(r => r.IsDefault).Id.ShouldBe(b.Id);
        _table.Rows.Single(r => r.Id == a.Id).IsDefault.ShouldBeFalse();
        await Should.ThrowAsync<PackageStoreNotFoundException>(() => _service.SetDefaultAsync(999, CancellationToken.None));
    }

    [Fact]
    public async Task Test_PassesTheStoredSecretsToTheProvider_AndRecordsTheOutcomeOnTheStore()
    {
        var created = await _service.CreateAsync(NewFake(), CancellationToken.None);

        var result = await _service.TestAsync(created.Id, CancellationToken.None);

        result.Success.ShouldBeTrue();
        _fake.LastConfiguration.Secret("Token").ShouldBe(PlaintextToken);

        var dto = await _service.GetAsync(created.Id, CancellationToken.None);

        dto.LastTestSucceeded.ShouldBe(true);
        dto.LastTestMessage.ShouldBe("fine");
        dto.LastTestedDate.ShouldNotBeNull();
    }

    [Fact]
    public async Task Get_OfAMissingStore_ThrowsNotFound()
    {
        await Should.ThrowAsync<PackageStoreNotFoundException>(() => _service.GetAsync(7, CancellationToken.None));
    }

    [Fact]
    public async Task Get_ForAFileSystemStore_ReportsNoSecretFields_AndWarningsComeFromTheProvider()
    {
        var seeded = _table.Seed("Local disk", FileSystemPackageBlobStore.StoreKind, isDefault: true);

        var dto = await _service.GetAsync(seeded.Id, CancellationToken.None);

        dto.SecretHasValue.ShouldBeEmpty();
        dto.Warnings.ShouldNotBeNull();
    }

    [Fact]
    public async Task List_OfAStoreWhoseKindIsNoLongerImplemented_StillListsItWithoutSecretValues()
    {
        _table.Seed("Orphan", "Quantum", "{}", "{\"Key\":\"abc-secret\"}");

        var dto = (await _service.ListAsync(CancellationToken.None)).Single();

        dto.SecretHasValue.ShouldBe(new Dictionary<string, bool> { ["Key"] = true });
        JsonSerializer.Serialize(dto).ShouldNotContain("abc-secret");
    }

    [Fact]
    public void DescribeProviders_ListsEveryProvider_SortedByName()
    {
        _service.DescribeProviders().Select(d => d.Kind).ShouldBe([FakeSecretBlobStoreProvider.FakeKind, FileSystemPackageBlobStore.StoreKind]);
    }

    private static TestPackageStoreSettingsCommand Unsaved(Dictionary<string, string> secrets = null, int? storeId = null, Dictionary<string, string> properties = null)
    {
        return new TestPackageStoreSettingsCommand
        {
            Kind = FakeSecretBlobStoreProvider.FakeKind, StoreId = storeId,
            Properties = properties ?? new Dictionary<string, string> { ["Endpoint"] = "https://unsaved" },
            Secrets = secrets ?? new Dictionary<string, string> { ["Token"] = "typed-token" }
        };
    }

    [Fact]
    public async Task TestSettings_RunsTheProviderTestOnTheUnsavedSettings_AndWritesNothing()
    {
        var result = await _service.TestSettingsAsync(Unsaved(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        _fake.LastConfiguration.Property("Endpoint").ShouldBe("https://unsaved");
        _fake.LastConfiguration.Secret("Token").ShouldBe("typed-token");
        _table.Rows.ShouldBeEmpty("testing unsaved settings must not create a store");
    }

    [Fact]
    public async Task TestSettings_ReturnsTheValidationProblems_InsteadOfTesting()
    {
        var command = Unsaved(secrets: new Dictionary<string, string>(), properties: new Dictionary<string, string> { ["Surprise"] = "x" });

        var ex = await Should.ThrowAsync<PackageStoreConfigurationInvalidException>(() => _service.TestSettingsAsync(command, CancellationToken.None));

        ex.Problems.Count.ShouldBe(3, "unknown property, missing Endpoint, missing Token");
        _fake.LastConfiguration.ShouldBeNull("the provider must not be called for invalid settings");
    }

    [Fact]
    public async Task TestSettings_WithAStoreId_FillsEmptySecretsFromTheStoredOnes_AndLetsTypedOnesWin()
    {
        var saved = await _service.CreateAsync(NewFake(), CancellationToken.None);
        var emptySecrets = new Dictionary<string, string> { ["Token"] = "" };

        await _service.TestSettingsAsync(Unsaved(emptySecrets, saved.Id), CancellationToken.None);

        _fake.LastConfiguration.Secret("Token").ShouldBe(PlaintextToken);
        _fake.LastConfiguration.Property("Endpoint").ShouldBe("https://unsaved", "the unsaved non-secret value is what is tested");

        await _service.TestSettingsAsync(Unsaved(storeId: saved.Id), CancellationToken.None);

        _fake.LastConfiguration.Secret("Token").ShouldBe("typed-token");
    }

    [Fact]
    public async Task TestSettings_WithAStoreId_DoesNotChangeTheSavedStore()
    {
        var saved = await _service.CreateAsync(NewFake(), CancellationToken.None);
        var before = _table.Rows.Single();
        var snapshot = (before.Name, before.Properties, before.Secret, before.LastModifiedDate, before.LastTestedDate, before.LastTestSucceeded, before.LastTestMessage);

        await _service.TestSettingsAsync(Unsaved(new Dictionary<string, string>(), saved.Id), CancellationToken.None);

        var after = _table.Rows.Single();

        (after.Name, after.Properties, after.Secret, after.LastModifiedDate, after.LastTestedDate, after.LastTestSucceeded, after.LastTestMessage).ShouldBe(snapshot);
    }

    [Fact]
    public async Task TestSettings_WithAStoreOfAnotherKind_OrAMissingStore_IsRejected()
    {
        var disk = _table.Seed("Local disk", FileSystemPackageBlobStore.StoreKind, isDefault: true);

        await Should.ThrowAsync<PackageStoreConfigurationInvalidException>(() => _service.TestSettingsAsync(Unsaved(storeId: disk.Id), CancellationToken.None));
        await Should.ThrowAsync<PackageStoreNotFoundException>(() => _service.TestSettingsAsync(Unsaved(storeId: 999), CancellationToken.None));
    }

    [Fact]
    public async Task TestSettings_WithAnUnknownKind_ThrowsProviderNotFound()
    {
        var command = Unsaved();
        command.Kind = "Quantum";

        await Should.ThrowAsync<PackageStoreProviderNotFoundException>(() => _service.TestSettingsAsync(command, CancellationToken.None));
    }
}
