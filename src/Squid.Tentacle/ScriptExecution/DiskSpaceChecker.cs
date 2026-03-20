using Serilog;

namespace Squid.Tentacle.ScriptExecution;

public static class DiskSpaceChecker
{
    private const double MinFreePercentage = 0.10;
    private const long MinFreeBytes = 1024 * 1024;

    internal static bool Enabled { get; set; } = true;

    public static void EnsureDiskHasEnoughFreeSpace(string path)
    {
        if (!Enabled) return;

        var (available, total) = GetDiskSpace(path);

        if (total <= 0) return;

        var freePercentage = (double)available / total;
        var requiredBytes = Math.Max((long)(total * MinFreePercentage), MinFreeBytes);

        if (available < requiredBytes)
        {
            throw new IOException(
                $"Insufficient disk space on {path}. " +
                $"Available: {FormatBytes(available)} ({freePercentage:P1}), " +
                $"Required: {FormatBytes(requiredBytes)} ({MinFreePercentage:P0} of {FormatBytes(total)})");
        }

        Log.Debug("Disk space check passed for {Path}: {Available} available ({Percentage:P1})",
            path, FormatBytes(available), freePercentage);
    }

    internal static (long Available, long Total) GetDiskSpace(string path)
    {
        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(path) ?? path);

            return (driveInfo.AvailableFreeSpace, driveInfo.TotalSize);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to check disk space for {Path}", path);
            return (0, 0);
        }
    }

    internal static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
            >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            >= 1024L => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B"
        };
    }
}
