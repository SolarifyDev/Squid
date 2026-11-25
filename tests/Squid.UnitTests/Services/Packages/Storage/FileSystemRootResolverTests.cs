using System.IO;
using System.Linq;
using Squid.Core.Services.Packages.Storage.Providers.FileSystem;

namespace Squid.UnitTests.Services.Packages.Storage;

public sealed class FileSystemRootResolverTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "squid-app");
    private static readonly string Data = Path.Combine(Path.GetTempPath(), "squid-data");

    [Fact]
    public void ExplicitRoot_WinsOverEverythingElse()
    {
        var explicitRoot = Path.Combine(Path.GetTempPath(), "elsewhere", "pkgs");

        FileSystemRootResolver.Resolve(explicitRoot, Data, Base).ShouldBe(explicitRoot);
    }

    [Fact]
    public void ExplicitRoot_TrailingSeparatorIsDropped_RelativeRootIsTakenFromTheBaseDirectory()
    {
        FileSystemRootResolver.Resolve(Path.Combine("rel", "pkgs") + Path.DirectorySeparatorChar, null, Base).ShouldBe(Path.Combine(Base, "rel", "pkgs"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyRoot_WithDataDirectory_ResolvesUnderDataDirectory(string configuredRoot)
    {
        FileSystemRootResolver.Resolve(configuredRoot, Data, Base).ShouldBe(Path.Combine(Data, "packages"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyRoot_WithoutDataDirectory_ResolvesBesideTheApplication(string dataDirectory)
    {
        FileSystemRootResolver.Resolve(null, dataDirectory, Base).ShouldBe(Path.Combine(Base, "packages"));
    }

    private const string App = "/opt/squid/app";

    [Fact]
    public void EmptyRoot_WithNoDataDirectory_LandsInTheApplicationFolder_AndWarns()
    {
        var root = FileSystemRootResolver.Resolve(null, null, App);

        FileSystemRootResolver.FindApplicationFolderWarning(root, App).ShouldNotBeNull().ShouldContain("data directory");
    }

    [Fact]
    public void EmptyRoot_WithADataDirectory_DoesNotWarn()
    {
        var root = FileSystemRootResolver.Resolve(null, "/data", App);

        FileSystemRootResolver.FindApplicationFolderWarning(root, App).ShouldBeNull();
    }

    [Theory]
    [InlineData("/opt/squid/app/packages")]
    [InlineData("/opt/squid/app/deep/er/packages")]
    [InlineData("/opt/squid/app")]
    [InlineData("/opt/squid/app/")]
    [InlineData("/opt/squid/app/../app/packages")]
    public void ExplicitRootInsideTheApplicationFolder_Warns(string explicitRoot)
    {
        var root = FileSystemRootResolver.Resolve(explicitRoot, "/data", App);

        FileSystemRootResolver.FindApplicationFolderWarning(root, App + "/").ShouldNotBeNull();
    }

    [Theory]
    [InlineData("/srv/packages")]
    [InlineData("/opt/squid")]
    [InlineData("/opt/squid/app2")]
    [InlineData("/opt/squid/app-data/packages")]
    [InlineData("/opt/squid/appx")]
    public void ExplicitRootElsewhere_OrOnlySharingANamePrefix_DoesNotWarn(string explicitRoot)
    {
        var root = FileSystemRootResolver.Resolve(explicitRoot, null, App);

        FileSystemRootResolver.FindApplicationFolderWarning(root, App).ShouldBeNull();
    }

    [Theory]
    [InlineData("", App)]
    [InlineData("/opt/squid/app/packages", "")]
    public void MissingInputs_GiveNoWarning(string root, string baseDirectory)
    {
        FileSystemRootResolver.FindApplicationFolderWarning(root, baseDirectory).ShouldBeNull();
    }
}
