using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Squid.Core.Services.Packages.Storage;
using Squid.Core.Services.Packages.Storage.Providers.FileSystem;
using Squid.Core.Settings.Setup;
using Squid.Message.Enums.PackageStorage;
using Squid.Message.Models.PackageStorage;

namespace Squid.UnitTests.Services.Packages.Storage;

public sealed class FileSystemProviderContractTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"squid-provider-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static FileSystemPackageBlobStoreProvider Provider(string dataDirectory = null)
    {
        var values = dataDirectory == null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["DataDirectory"] = dataDirectory };

        return new FileSystemPackageBlobStoreProvider(new DataDirectorySetting(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
    }

    private static PackageBlobStoreConfiguration Config(string root = null)
    {
        return new PackageBlobStoreConfiguration { Properties = root == null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Root"] = root } };
    }

    /// <summary>Saved store records refer to these strings; changing one silently orphans every saved store. Rename only together with a data migration.</summary>
    [Fact]
    public void KindAndFieldKeys_ArePinned()
    {
        var descriptor = Provider().Describe();

        FileSystemPackageBlobStore.StoreKind.ShouldBe("FileSystem");
        FileSystemPackageBlobStoreProvider.RootKey.ShouldBe("Root");
        descriptor.Kind.ShouldBe("FileSystem");
        descriptor.Fields.Select(f => f.Key).ShouldBe(["Root"]);
        descriptor.Fields.Single().Kind.ShouldBe(PackageStoreFieldKind.Path);
        descriptor.Fields.Single().Required.ShouldBeFalse("an empty root means: decide at runtime");
        descriptor.DisplayName.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ProbePrefix_IsPinned()
    {
        PackageBlobStoreProbe.KeyPrefix.ShouldBe(".squid-probe/");
    }

    [Fact]
    public void Create_UsesTheExplicitRoot_OrTheDataDirectory_OrBesideTheApplication()
    {
        ((FileSystemPackageBlobStore)Provider().Create(Config(_temp))).Root.ShouldBe(_temp);
        ((FileSystemPackageBlobStore)Provider(_temp).Create(Config())).Root.ShouldBe(Path.Combine(_temp, "packages"));
        ((FileSystemPackageBlobStore)Provider().Create(Config())).Root.ShouldBe(Path.Combine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), "packages"));
    }

    [Fact]
    public async Task Test_OnAWritableFolder_Succeeds_AndLeavesNoProbeFileBehind()
    {
        Directory.CreateDirectory(_temp);

        var result = await Provider().TestAsync(Config(_temp), CancellationToken.None);

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain(_temp);
        Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories).ShouldBeEmpty("the probe key must be deleted again");
    }

    [Fact]
    public async Task Test_WhenTheFolderCannotBeUsed_ReportsFailureInsteadOfThrowing()
    {
        Directory.CreateDirectory(_temp);

        var blockingFile = Path.Combine(_temp, "i-am-a-file");
        await File.WriteAllTextAsync(blockingFile, "x");

        var result = await Provider().TestAsync(Config(Path.Combine(blockingFile, "pkgs")), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>The frontend reads the field kind by name; the number would silently change meaning if the enum were reordered.</summary>
    [Fact]
    public void FieldKind_IsSentByName_AndReadBackFromTheName()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Provider().Describe(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        json.ShouldContain("\"kind\":\"Path\"");
        json.ShouldNotContain("\"kind\":3");

        var back = System.Text.Json.JsonSerializer.Deserialize<PackageStoreFieldDescriptor>("{\"key\":\"K\",\"kind\":\"Secret\"}", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        back.Kind.ShouldBe(PackageStoreFieldKind.Secret);
    }

    [Fact]
    public async Task TestSettings_Through_TheService_ReallyRunsTheFileSystemProbeOnAnUnsavedFolder()
    {
        var table = new Support.FakePackageStoreDataProvider();
        var service = new PackageStoreService(table, [Provider()], []);

        var result = await service.TestSettingsAsync(new Squid.Message.Commands.PackageStorage.TestPackageStoreSettingsCommand { Kind = "FileSystem", Properties = new Dictionary<string, string> { ["Root"] = _temp } }, CancellationToken.None);

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("does not exist yet");
        Directory.Exists(_temp).ShouldBeFalse("the folder did not exist before the test, so the test removes what it created");
        table.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void GetWarnings_SurfacesTheApplicationFolderWarning_ForARootInsideTheApplicationFolder()
    {
        var inside = Path.Combine(AppContext.BaseDirectory, "packages");

        Provider().GetWarnings(Config(inside)).ShouldContain(w => w.Contains("inside the application folder"));
        Provider().GetWarnings(Config(_temp)).ShouldNotContain(w => w.Contains("inside the application folder"));
    }

    [Fact]
    public void ValidateConfiguration_AcceptsTheDefaultAndAnOrdinaryFolder_AndRejectsTheRest()
    {
        Provider().ValidateConfiguration(Config()).ShouldBeEmpty();
        Provider().ValidateConfiguration(Config(_temp)).ShouldBeEmpty();
        Provider().ValidateConfiguration(Config("/etc")).ShouldNotBeEmpty();
        Provider().ValidateConfiguration(Config("rel/path")).ShouldNotBeEmpty();
        Provider().ValidateConfiguration(Config("/srv/\0x")).ShouldNotBeEmpty();
        Provider().ValidateConfiguration(Config(AppContext.BaseDirectory)).ShouldNotBeEmpty("an explicit root may not be the application folder");
    }

    [Fact]
    public void Create_WithAnInvalidRoot_ThrowsTheSpecificException_NotAnArgumentException()
    {
        Should.Throw<Squid.Core.Services.Packages.Storage.Exceptions.PackageStoreConfigurationInvalidException>(() => Provider().Create(Config("/srv/\0x")));
    }

    [Fact]
    public void GetWarnings_ForAnUnresolvableRoot_ReturnsAWarning_InsteadOfThrowing()
    {
        var warnings = Provider().GetWarnings(Config("/srv/\0x"));

        warnings.ShouldHaveSingleItem().ShouldContain("settings are invalid");
    }

    [Fact]
    public async Task Test_WithAnInvalidRoot_ReportsFailure_WithoutTouchingTheDisk()
    {
        var result = await Provider().TestAsync(Config("/etc"), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("system folder");
    }

    [Fact]
    public void TheEmptyDefaultBesideTheApplication_StaysValid_SoExistingInstallsKeepWorking_AndOnlyWarns()
    {
        Provider().ValidateConfiguration(Config()).ShouldBeEmpty();
        Provider().GetWarnings(Config()).ShouldContain(w => w.Contains("inside the application folder"));
    }

    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>The settings page reads these exact names. Renaming a property is a breaking change for it.</summary>
    [Fact]
    public void WireNames_OfTheDescriptor_TheStoreDto_AndTheTestResult_ArePinned()
    {
        var descriptor = System.Text.Json.JsonSerializer.Serialize(new PackageStoreProviderDescriptor
        {
            Kind = "K", DisplayName = "D", Description = "d",
            Fields = [new PackageStoreFieldDescriptor { Key = "Token", Label = "L", Kind = PackageStoreFieldKind.Secret, Required = true, IsLocation = true, BoundTo = ["Endpoint"], Options = ["a"], Default = "x", Placeholder = "p", HelpText = "h" }]
        }, Web);

        foreach (var name in new[] { "\"kind\":\"K\"", "\"displayName\":", "\"description\":", "\"fields\":[", "\"key\":\"Token\"", "\"label\":", "\"kind\":\"Secret\"", "\"required\":true", "\"isLocation\":true", "\"boundTo\":[\"Endpoint\"]", "\"options\":[\"a\"]", "\"default\":", "\"placeholder\":", "\"helpText\":" })
            descriptor.ShouldContain(name);

        var dto = System.Text.Json.JsonSerializer.Serialize(new PackageStoreDto { Location = "/data/packages", Warnings = ["w"], SecretHasValue = new Dictionary<string, bool> { ["Token"] = true } }, Web);

        foreach (var name in new[] { "\"location\":\"/data/packages\"", "\"warnings\":[\"w\"]", "\"secretHasValue\":{\"Token\":true}", "\"isDefault\":", "\"properties\":", "\"lastTestSucceeded\":", "\"lastTestMessage\":", "\"lastTestedDate\":" })
            dto.ShouldContain(name);

        var result = System.Text.Json.JsonSerializer.Serialize(new PackageBlobStoreTestResult { Success = true, Message = "m", Warnings = ["careful"] }, Web);

        result.ShouldContain("\"success\":true");
        result.ShouldContain("\"message\":\"m\"");
        result.ShouldContain("\"warnings\":[\"careful\"]");
    }

    [Fact]
    public void TheFileSystemRootField_IsMarkedAsTheLocation_AndNoSecretFieldIsBound()
    {
        var field = Provider().Describe().Fields.Single();

        field.IsLocation.ShouldBeTrue();
        field.BoundTo.ShouldBeEmpty();
    }

    [Fact]
    public void DescribeLocation_IsTheExplicitRoot_TheResolvedDefault_OrNullWhenUnresolvable()
    {
        Provider().DescribeLocation(Config(_temp)).ShouldBe(_temp);
        Provider(_temp).DescribeLocation(Config()).ShouldBe(Path.Combine(_temp, "packages"));
        Provider().DescribeLocation(Config()).ShouldBe(Path.Combine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), "packages"));
        Provider().DescribeLocation(Config("/srv/\0x")).ShouldBeNull();
        Provider().DescribeLocation(Config("/etc")).ShouldBeNull();
    }

    [Fact]
    public async Task Test_OfAMissingDeepRoot_RemovesEveryFolderItCreated_ButNotTheExistingAncestor()
    {
        Directory.CreateDirectory(_temp);
        var deep = Path.Combine(_temp, "a", "b", "packages");

        var result = await Provider().TestAsync(Config(deep), CancellationToken.None);

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("does not exist yet");
        Directory.Exists(Path.Combine(_temp, "a")).ShouldBeFalse();
        Directory.Exists(_temp).ShouldBeTrue("the nearest folder that already existed stays");
    }

    [Fact]
    public async Task Test_OfAnExistingRoot_KeepsItsFilesAndRemovesOnlyTheProbeFolders()
    {
        Directory.CreateDirectory(Path.Combine(_temp, "acme"));
        await File.WriteAllTextAsync(Path.Combine(_temp, "acme", "web.zip"), "x");

        var result = await Provider().TestAsync(Config(_temp), CancellationToken.None);

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldNotContain("does not exist yet");
        File.Exists(Path.Combine(_temp, "acme", "web.zip")).ShouldBeTrue();
        Directory.GetDirectories(_temp).Select(Path.GetFileName).ShouldBe(["acme"]);
    }
}
