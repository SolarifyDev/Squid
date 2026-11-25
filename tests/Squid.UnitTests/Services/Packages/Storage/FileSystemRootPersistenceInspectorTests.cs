using System.IO;
using System.Linq;
using Squid.Core.Services.Packages.Storage.Providers.FileSystem;

namespace Squid.UnitTests.Services.Packages.Storage;

public sealed class FileSystemRootPersistenceInspectorTests
{
    private const string OverlayRoot = "1010 1000 0:60 / / rw,relatime master:30 - overlay overlay rw,lowerdir=/var/lib/docker/overlay2/l/AAA,upperdir=/var/lib/docker/overlay2/x/diff,workdir=/var/lib/docker/overlay2/x/work";
    private const string Proc = "1011 1010 0:63 / /proc rw,nosuid,nodev,noexec,relatime - proc proc rw";
    private const string AnonymousData = "1020 1010 254:1 /docker/volumes/47391b86f3f14de6988e273f5766920129bd488a2065e45b95845d966c3ef4ad/_data /data rw,relatime master:29 - ext4 /dev/vda1 rw";
    private const string NamedData = "1020 1010 254:1 /docker/volumes/squid-data/_data /data rw,relatime master:29 - ext4 /dev/vda1 rw";
    private const string BindData = "1020 1010 254:1 /home/ops/squid /data rw,relatime - ext4 /dev/vda1 rw";
    private const string AnonymousPackages = "1021 1010 254:1 /docker/volumes/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/_data /data/packages rw - ext4 /dev/vda1 rw";
    private const string NamedPackages = "1021 1010 254:1 /docker/volumes/squid-packages/_data /data/packages rw - ext4 /dev/vda1 rw";
    private const string HostDisk = "30 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw,errors=remount-ro";

    private static string MountInfo(params string[] lines) => string.Join('\n', lines) + "\n";

    [Fact]
    public void RootOnTheContainersOwnOverlayFilesystem_Warns()
    {
        var warning = FileSystemRootPersistenceInspector.FindWarning("/data/packages", MountInfo(OverlayRoot, Proc));

        warning.ShouldNotBeNull();
        warning.ShouldContain("/data/packages");
        warning.ShouldContain("lost when the container is recreated");
        warning.ShouldNotContain("anonymous");
    }

    [Fact]
    public void RootUnderAnAnonymousVolumeMountedAtItsParent_WarnsAboutTheAnonymousVolume()
    {
        var warning = FileSystemRootPersistenceInspector.FindWarning("/data/packages", MountInfo(OverlayRoot, Proc, AnonymousData));

        warning.ShouldNotBeNull();
        warning.ShouldContain("anonymous Docker volume (47391b86f3f1...)");
        warning.ShouldContain("-v squid-data:/data");
    }

    [Fact]
    public void RootAtAnAnonymousVolumeMountPoint_Warns()
    {
        var warning = FileSystemRootPersistenceInspector.FindWarning("/data/packages", MountInfo(OverlayRoot, AnonymousData, AnonymousPackages));

        warning.ShouldNotBeNull();
        warning.ShouldContain("aaaaaaaaaaaa...");
        warning.ShouldContain("-v squid-data:/data/packages");
    }

    [Theory]
    [InlineData(NamedData)]
    [InlineData(BindData)]
    public void RootUnderANamedVolumeOrBindMount_DoesNotWarn(string dataMount)
    {
        FileSystemRootPersistenceInspector.FindWarning("/data/packages", MountInfo(OverlayRoot, Proc, dataMount)).ShouldBeNull();
    }

    [Fact]
    public void ADeeperPersistentMount_WinsOverAnAnonymousParentMount()
    {
        FileSystemRootPersistenceInspector.FindWarning("/data/packages", MountInfo(OverlayRoot, AnonymousData, NamedPackages)).ShouldBeNull();
    }

    [Fact]
    public void ADeeperMountAtADifferentPath_DoesNotCoverTheRoot()
    {
        FileSystemRootPersistenceInspector.FindWarning("/srv/packages", MountInfo(OverlayRoot, AnonymousData, NamedPackages)).ShouldNotBeNull();
    }

    [Fact]
    public void AMountPointThatIsOnlyAPrefixOfTheName_DoesNotCoverTheRoot()
    {
        var mountInfo = MountInfo(OverlayRoot, "1022 1010 254:1 /docker/volumes/squid-d/_data /dat rw - ext4 /dev/vda1 rw");

        FileSystemRootPersistenceInspector.FindWarning("/data/packages", mountInfo).ShouldNotBeNull("/dat is not a parent of /data/packages; the overlay root is");
    }

    [Fact]
    public void RootOnAHostDisk_DoesNotWarn()
    {
        FileSystemRootPersistenceInspector.FindWarning("/var/lib/squid/packages", MountInfo(HostDisk, Proc)).ShouldBeNull();
    }

    [Fact]
    public void RootWithATrailingSeparator_IsTreatedLikeTheFolder()
    {
        FileSystemRootPersistenceInspector.FindWarning("/data/packages/", MountInfo(OverlayRoot, AnonymousData)).ShouldNotBeNull();
    }

    [Fact]
    public void AMountPointWithAnEscapedSpace_IsMatched()
    {
        var spaced = "1020 1010 254:1 /docker/volumes/47391b86f3f14de6988e273f5766920129bd488a2065e45b95845d966c3ef4ad/_data /my\\040data rw - ext4 /dev/vda1 rw";

        FileSystemRootPersistenceInspector.FindWarning("/my data/packages", MountInfo(OverlayRoot, spaced)).ShouldContain("anonymous");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("garbage\nnot a mountinfo line\n")]
    public void UnreadableOrEmptyMountInfo_GivesNoWarning(string mountInfo)
    {
        FileSystemRootPersistenceInspector.FindWarning("/data/packages", mountInfo).ShouldBeNull();
    }

    [Fact]
    public void EmptyRoot_GivesNoWarning()
    {
        FileSystemRootPersistenceInspector.FindWarning("", MountInfo(OverlayRoot)).ShouldBeNull();
    }
}
