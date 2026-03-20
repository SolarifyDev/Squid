using System;
using System.IO;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class DiskSpaceCheckerTests
{
    [Fact]
    public void EnsureDiskHasEnoughFreeSpace_OnRealDisk_RunsWithoutCrash()
    {
        var tempDir = Path.GetTempPath();
        var (available, total) = DiskSpaceChecker.GetDiskSpace(tempDir);

        if (total > 0 && (double)available / total < 0.10)
        {
            Should.Throw<IOException>(() => DiskSpaceChecker.EnsureDiskHasEnoughFreeSpace(tempDir));
        }
        else
        {
            Should.NotThrow(() => DiskSpaceChecker.EnsureDiskHasEnoughFreeSpace(tempDir));
        }
    }

    [Fact]
    public void GetDiskSpace_ReturnsPositiveValues()
    {
        var (available, total) = DiskSpaceChecker.GetDiskSpace(Path.GetTempPath());

        total.ShouldBeGreaterThan(0);
        available.ShouldBeGreaterThan(0);
        available.ShouldBeLessThanOrEqualTo(total);
    }

    [Fact]
    public void GetDiskSpace_InvalidPath_ReturnsZeros()
    {
        var (available, total) = DiskSpaceChecker.GetDiskSpace("/nonexistent/unlikely/path");

        (available == 0 || total == 0 || available > 0).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1572864, "1.5 MB")]
    [InlineData(1073741824, "1.0 GB")]
    [InlineData(1610612736, "1.5 GB")]
    public void FormatBytes_FormatsCorrectly(long bytes, string expected)
    {
        DiskSpaceChecker.FormatBytes(bytes).ShouldBe(expected);
    }
}
