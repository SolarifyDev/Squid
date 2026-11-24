using Squid.Core.Setup;

namespace Squid.UnitTests.Setup;

public sealed class DataVolumeInspectorTests
{
    private const string Anonymous = "814 806 254:1 /docker/volumes/47391b86f3f14de6988e273f5766920129bd488a2065e45b95845d966c3ef4ad/_data /data rw,relatime master:29 - ext4 /dev/vda1 rw";
    private const string Named = "814 806 254:1 /docker/volumes/squid-data/_data /data rw,relatime master:29 - ext4 /dev/vda1 rw";
    private const string Bind = "814 806 254:1 /home/ops/squid /data rw,relatime - ext4 /dev/vda1 rw";
    private const string Elsewhere = "814 806 254:1 /docker/volumes/47391b86f3f14de6988e273f5766920129bd488a2065e45b95845d966c3ef4ad/_data /other rw - ext4 /dev/vda1 rw";

    [Theory]
    [InlineData(Anonymous, true)]
    [InlineData(Named, false)]
    [InlineData(Bind, false)]
    [InlineData(Elsewhere, false)]
    [InlineData("", false)]
    public void WarnsOnlyForAnonymousVolumeMountedAtTheDataDirectory(string mountInfo, bool expectWarning)
    {
        var warning = DataVolumeInspector.FindAnonymousVolumeWarning("/data", "1 2 3:4 / / rw - x y rw\n" + mountInfo);

        (warning != null).ShouldBe(expectWarning);
        if (expectWarning) warning.ShouldContain("-v squid-data:/data");
    }
}
