using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Exceptions;
using Squid.Core.Setup.Install;
using Squid.Message.Models.Setup;
using Squid.UnitTests.Services.Packages.Storage.Support;

namespace Squid.UnitTests.Setup;

public sealed class SetupPackageStorageTests
{
    private static readonly SetupCapabilities Everything = new(true, true);
    private static readonly IReadOnlyList<IPackageBlobStoreProvider> Providers = new IPackageBlobStoreProvider[] { new FakeSecretBlobStoreProvider() };

    private static SetupInstallRequest Request(SetupPackageStorageChoice storage) => new()
    {
        Database = new SetupDatabaseChoice { Mode = SetupServiceMode.Builtin },
        Redis = new SetupRedisChoice { Mode = SetupServiceMode.Builtin },
        Admin = new SetupAdminAccount { UserName = "admin", Password = "longenough" },
        Server = new SetupServerUrls { ExternalUrl = "http://squid.local:8080" },
        PackageStorage = storage
    };

    private static SetupPackageStorageChoice Fake(Dictionary<string, string> properties, Dictionary<string, string> secrets) => new() { Kind = FakeSecretBlobStoreProvider.FakeKind, Properties = properties, Secrets = secrets };

    [Fact]
    public void NoPackageStorage_IsValid_ForExistingRequestShapes() => Should.NotThrow(() => SetupRequestValidator.EnsureValid(Request(null), Everything));

    [Fact]
    public void ValidChoice_Passes()
    {
        var choice = Fake(new() { ["Endpoint"] = "https://x" }, new() { ["Token"] = "t" });

        Should.NotThrow(() => SetupRequestValidator.EnsureValid(Request(choice), Everything, Providers));
    }

    [Fact]
    public void UnknownKind_IsRejectedBeforeInstalling()
    {
        var ex = Should.Throw<SetupValidationException>(() => SetupRequestValidator.EnsureValid(Request(new SetupPackageStorageChoice { Kind = "Nope" }), Everything, Providers));

        ex.Errors.Keys.ShouldBe(new[] { "packageStorage.kind" });
    }

    [Fact]
    public void MissingRequiredField_IsRejected_NamingTheField()
    {
        var ex = Should.Throw<SetupValidationException>(() => SetupRequestValidator.EnsureValid(Request(Fake(new() { ["Endpoint"] = "https://x" }, new())), Everything, Providers));

        ex.Errors["packageStorage"].ShouldContain("Token");
    }

    [Fact]
    public void UnknownSetting_IsRejected()
    {
        var ex = Should.Throw<SetupValidationException>(() => SetupRequestValidator.EnsureValid(Request(new SetupPackageStorageChoice { Kind = "FileSystem", Properties = new() { ["Bogus"] = "x" } }), Everything));

        ex.Errors.Keys.ShouldBe(new[] { "packageStorage" });
    }

    [Fact]
    public void PackageStorageProblems_AreReportedAlongsideOtherOnes()
    {
        var request = Request(new SetupPackageStorageChoice { Kind = "Nope" }) with { Admin = new SetupAdminAccount { UserName = "a", Password = "x" } };

        var ex = Should.Throw<SetupValidationException>(() => SetupRequestValidator.EnsureValid(request, Everything));

        ex.Errors.Keys.ShouldBe(new[] { "admin.password", "packageStorage.kind" }, ignoreOrder: true);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("FileSystem", "", true)]
    [InlineData("FileSystem", "  ", true)]
    [InlineData("FileSystem", "/data/pkgs", false)]
    [InlineData("Other", "", false)]
    public void IsSeededDefault_OnlyForNoChoiceOrLocalDiskWithEmptyRoot(string kind, string root, bool expected)
    {
        var choice = kind == null ? null : new SetupPackageStorageChoice { Kind = kind, Properties = new() { ["Root"] = root } };

        SetupPackageStorage.IsSeededDefault(choice).ShouldBe(expected);
    }

    [Fact]
    public void IsSeededDefault_FalseWhenASecretIsGiven()
    {
        SetupPackageStorage.IsSeededDefault(new SetupPackageStorageChoice { Kind = "FileSystem", Secrets = new() { ["Token"] = "t" } }).ShouldBeFalse();
    }

    [Fact]
    public async Task SettingsCheck_RunsTheProviderTestWithoutDatabaseOrContainer()
    {
        var provider = new FakeSecretBlobStoreProvider();

        var result = await PackageStoreSettingsCheck.TestAsync(provider, new Dictionary<string, string> { ["Endpoint"] = "e" }, new Dictionary<string, string> { ["Token"] = "t" }, new Dictionary<string, string>(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        provider.LastConfiguration.Secret("Token").ShouldBe("t");
    }

    [Fact]
    public async Task SettingsCheck_InvalidSettings_ThrowsBeforeTouchingTheProvider()
    {
        var provider = new FakeSecretBlobStoreProvider();

        await Should.ThrowAsync<PackageStoreConfigurationInvalidException>(() => PackageStoreSettingsCheck.TestAsync(provider, new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>(), CancellationToken.None));

        provider.LastConfiguration.ShouldBeNull();
    }
}
