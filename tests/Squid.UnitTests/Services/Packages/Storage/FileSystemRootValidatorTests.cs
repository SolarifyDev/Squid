using System.IO;
using System.Linq;
using Squid.Core.Services.Packages.Storage.Providers.FileSystem;

namespace Squid.UnitTests.Services.Packages.Storage;

public sealed class FileSystemRootValidatorTests
{
    private const string App = "/opt/squid/app/";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyRoot_MeansTheDefault_AndIsAlwaysFine(string root)
    {
        FileSystemRootValidator.Validate(root, App).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/srv/packages")]
    [InlineData("/usr/local/squid-packages")]
    [InlineData("/var/lib/squid/packages")]
    [InlineData("/data/packages/")]
    [InlineData("/opt/squid/app2")]
    [InlineData("/opt/squid/app/packages")]
    public void OrdinaryAbsoluteFolders_AreFine_EvenInsideTheApplicationFolder(string root)
    {
        if (Path.DirectorySeparatorChar != '/') return;

        FileSystemRootValidator.Validate(root, App).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/srv/\0bad")]
    [InlineData("\0")]
    [InlineData("/srv/new\nline")]
    public void ACharacterThatCannotBeInAPath_IsRejected_WithoutThrowing(string root)
    {
        var problems = FileSystemRootValidator.Validate(root, App);

        problems.ShouldHaveSingleItem().ShouldContain("character");
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("packages")]
    [InlineData("./packages")]
    [InlineData("../packages")]
    public void ARelativeRoot_IsRejected(string root)
    {
        FileSystemRootValidator.Validate(root, App).ShouldContain(p => p.Contains("absolute"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/etc")]
    [InlineData("/etc/")]
    [InlineData("/bin")]
    [InlineData("/sbin")]
    [InlineData("/usr")]
    [InlineData("/var/lib")]
    [InlineData("/var/lib/")]
    [InlineData("/System")]
    [InlineData("/boot")]
    [InlineData("/root")]
    [InlineData("C:\\")]
    [InlineData("C:")]
    [InlineData("D:\\")]
    [InlineData("C:\\Windows")]
    [InlineData("c:/windows/")]
    [InlineData("C:\\Program Files")]
    public void SystemFoldersAndDriveRoots_AreRejected(string root)
    {
        FileSystemRootValidator.Validate(root, App).ShouldContain(p => p.Contains("system folder or a drive root"));
    }

    [Theory]
    [InlineData("/etc/../etc")]
    [InlineData("/srv/../etc")]
    [InlineData("/usr/local/..")]
    public void ASystemFolderReachedThroughDotDot_IsRejected(string root)
    {
        if (Path.DirectorySeparatorChar != '/') return;

        FileSystemRootValidator.Validate(root, App).ShouldContain(p => p.Contains("system folder"));
    }

    [Theory]
    [InlineData("/opt/squid/app")]
    [InlineData("/opt/squid/app/")]
    [InlineData("/opt/squid")]
    [InlineData("/opt")]
    [InlineData("/opt/squid/app/../app")]
    public void AnExplicitRoot_ThatIsOrHoldsTheApplicationFolder_IsRejected(string root)
    {
        if (Path.DirectorySeparatorChar != '/') return;

        FileSystemRootValidator.Validate(root, App).ShouldContain(p => p.Contains("contains the application folder"));
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("//", true)]
    [InlineData("/etc", true)]
    [InlineData("/etc/ssl", false)]
    [InlineData("/var", true)]
    [InlineData("/var/lib/squid", false)]
    [InlineData("/etcetera", false)]
    [InlineData("C:\\", true)]
    [InlineData("C:\\Windows\\System32", false)]
    [InlineData("C:\\Windows", true)]
    [InlineData("C:\\data\\packages", false)]
    [InlineData("", true)]
    public void IsSystemFolder_MatchesTheExplicitList_OnlyForTheFolderItself(string path, bool expected)
    {
        FileSystemRootValidator.IsSystemFolder(path).ShouldBe(expected);
    }

    [Fact]
    public void WithoutABaseDirectory_OnlyTheOtherRulesApply()
    {
        FileSystemRootValidator.Validate("/opt/squid/app", null).ShouldBeEmpty();
    }
}
