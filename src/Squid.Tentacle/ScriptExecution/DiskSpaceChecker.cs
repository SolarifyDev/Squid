using Serilog;

namespace Squid.Tentacle.ScriptExecution;

public static class DiskSpaceChecker
{
    private const double MinFreePercentage = 0.10;
    private const long MinFreeBytes = 1024 * 1024;

    /// <summary>Default ceiling on the free space a check may demand: volumes above 200 GiB would otherwise require
    /// more than this (10% of a 20 TB NFS export is 2 TB), failing every script on a volume with ample space.</summary>
    public const long DefaultMaxRequiredFreeBytes = 20L * 1024 * 1024 * 1024;

    internal static bool Enabled { get; set; } = true;

    internal static long MaxRequiredFreeBytes { get; private set; } = DefaultMaxRequiredFreeBytes;

    /// <summary>Applies <c>Tentacle:DiskSpaceMaxRequiredFreeBytes</c>; a value below the 1 MB floor (including 0 / negative) falls back to the default.</summary>
    public static void Configure(long maxRequiredFreeBytes)
    {
        MaxRequiredFreeBytes = maxRequiredFreeBytes < MinFreeBytes ? DefaultMaxRequiredFreeBytes : maxRequiredFreeBytes;
    }

    internal static long RequiredFreeBytes(long total)
    {
        return Math.Max(Math.Min((long)(total * MinFreePercentage), MaxRequiredFreeBytes), MinFreeBytes);
    }

    public static void EnsureDiskHasEnoughFreeSpace(string path)
    {
        if (!Enabled) return;

        EnsureEnoughFreeSpace(path);
    }

    internal static void EnsureEnoughFreeSpace(string path)
    {
        var (available, total) = GetDiskSpace(path);

        EnsureEnoughFreeSpace(path, available, total);
    }

    internal static void EnsureEnoughFreeSpace(string path, long available, long total)
    {
        if (total <= 0) return;

        var freePercentage = (double)available / total;
        var requiredBytes = RequiredFreeBytes(total);

        if (available < requiredBytes)
        {
            throw new IOException(
                $"Insufficient disk space on {path}. " +
                $"Available: {FormatBytes(available)} ({freePercentage:P1}), " +
                $"Required: {FormatBytes(requiredBytes)} ({MinFreePercentage:P0} of {FormatBytes(total)}, capped at {FormatBytes(MaxRequiredFreeBytes)})");
        }

        Log.Debug("Disk space check passed for {Path}: {Available} available ({Percentage:P1})",
            path, FormatBytes(available), freePercentage);
    }

    internal static (long Available, long Total) GetDiskSpace(string path)
    {
        try
        {
            var driveInfo = new DriveInfo(VolumeResolver.ResolveVolumeRoot(path) ?? Path.GetPathRoot(path) ?? path);

            return (driveInfo.AvailableFreeSpace, driveInfo.TotalSize);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to check disk space for {Path}", path);
            return (0, 0);
        }
    }

    public static bool HasSufficientSpace(string path)
    {
        try
        {
            EnsureDiskHasEnoughFreeSpace(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static WorkspaceUsage GetWorkspaceUsage(string path)
    {
        var (available, total) = GetDiskSpace(path);

        if (total <= 0)
            return new WorkspaceUsage(0, 0, 0);

        var used = total - available;
        return new WorkspaceUsage(used, total, available);
    }

    public record WorkspaceUsage(long UsedBytes, long TotalBytes, long FreeBytes)
    {
        public double FreePercentage => TotalBytes > 0 ? (double)FreeBytes / TotalBytes : 0;
        public bool IsLowSpace => TotalBytes > 0 && FreePercentage < MinFreePercentage && FreeBytes < MaxRequiredFreeBytes;
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
