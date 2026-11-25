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
}
