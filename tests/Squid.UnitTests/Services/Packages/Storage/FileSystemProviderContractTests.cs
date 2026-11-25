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
        Directory.Exists(_temp).ShouldBeTrue("the probe wrote into the unsaved folder");
        Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories).ShouldBeEmpty();
        table.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void GetWarnings_SurfacesTheApplicationFolderWarning_ForARootInsideTheApplicationFolder()
    {
        var inside = Path.Combine(AppContext.BaseDirectory, "packages");

        Provider().GetWarnings(Config(inside)).ShouldContain(w => w.Contains("inside the application folder"));
        Provider().GetWarnings(Config(_temp)).ShouldNotContain(w => w.Contains("inside the application folder"));
    }
}
