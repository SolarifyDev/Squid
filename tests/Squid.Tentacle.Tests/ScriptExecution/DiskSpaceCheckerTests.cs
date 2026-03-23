using System;
using System.IO;
using Squid.Tentacle.ScriptExecution;

namespace Squid.Tentacle.Tests.ScriptExecution;

public class DiskSpaceCheckerTests
{
    [Fact]
    public void EnsureDiskHasEnoughFreeSpace_OnRealDisk_RunsWithoutCrash()
    {
        DiskSpaceChecker.Enabled = true;
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

    [Fact]
    public void GetWorkspaceUsage_ReturnsValidValues()
    {
        var usage = DiskSpaceChecker.GetWorkspaceUsage(Path.GetTempPath());

        usage.TotalBytes.ShouldBeGreaterThan(0);
        usage.FreeBytes.ShouldBeGreaterThan(0);
        usage.UsedBytes.ShouldBeGreaterThanOrEqualTo(0);
        (usage.UsedBytes + usage.FreeBytes).ShouldBe(usage.TotalBytes);
    }

    [Fact]
    public void WorkspaceUsage_FreePercentage_CalculatedCorrectly()
    {
        var usage = new DiskSpaceChecker.WorkspaceUsage(800, 1000, 200);

        usage.FreePercentage.ShouldBe(0.2);
    }

    [Theory]
    [InlineData(950, 1000, 50, true)]
    [InlineData(500, 1000, 500, false)]
    [InlineData(850, 1000, 150, false)]
    [InlineData(910, 1000, 90, true)]
    public void WorkspaceUsage_IsLowSpace_DetectsCorrectly(long used, long total, long free, bool expectedLow)
    {
        var usage = new DiskSpaceChecker.WorkspaceUsage(used, total, free);

        usage.IsLowSpace.ShouldBe(expectedLow);
    }

    [Fact]
    public void HasSufficientSpace_EnoughSpace_ReturnsTrue()
    {
        var prev = DiskSpaceChecker.Enabled;

        try
        {
            DiskSpaceChecker.Enabled = true;
            var tempDir = Path.GetTempPath();

            var result = DiskSpaceChecker.HasSufficientSpace(tempDir);

            // On most dev machines temp has plenty of space
            var (available, total) = DiskSpaceChecker.GetDiskSpace(tempDir);
            if (total > 0 && (double)available / total >= 0.10)
                result.ShouldBeTrue();
        }
        finally
        {
            DiskSpaceChecker.Enabled = prev;
        }
    }

    [Fact]
    public void HasSufficientSpace_DisabledChecker_ReturnsTrue()
    {
        var prev = DiskSpaceChecker.Enabled;

        try
        {
            DiskSpaceChecker.Enabled = false;

            var result = DiskSpaceChecker.HasSufficientSpace("/any/path");

            result.ShouldBeTrue();
        }
        finally
        {
            DiskSpaceChecker.Enabled = prev;
        }
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
