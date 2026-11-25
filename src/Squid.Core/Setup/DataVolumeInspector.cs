using System.Text.RegularExpressions;

namespace Squid.Core.Setup;

/// <summary>
/// Best-effort detection of a data directory living in an ANONYMOUS Docker volume (the image's
/// <c>VOLUME /data</c> when <c>docker run</c> had no <c>-v</c>). Data there survives restarts but
/// not an upgrade that recreates the container, so the operator is told how to keep it.
/// Returns null whenever this cannot be determined (not Linux, no mountinfo, bind mount, ...).
/// </summary>
public static partial class DataVolumeInspector
{
    public const string MountInfoPath = "/proc/self/mountinfo";

    public static string FindAnonymousVolumeWarning(string dataDirectory, string mountInfo)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || string.IsNullOrEmpty(mountInfo)) return null;

        var volume = FindAnonymousVolumeId(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)), mountInfo);

        if (volume == null) return null;

        return $"{dataDirectory} is stored in an anonymous Docker volume ({volume[..12]}...). It survives restarts, but a new container created for an upgrade will not see it. Start the container with a named volume, e.g. -v squid-data:{dataDirectory}, or upgrade with --volumes-from <old container>.";
    }

    /// <summary>mountinfo fields: [3] = root of the mount in its filesystem, [4] = mount point.</summary>
    private static string FindAnonymousVolumeId(string mountPoint, string mountInfo)
    {
        var mounts = mountInfo.Split('\n').Select(l => l.Split(' ')).Where(f => f.Length > 4 && f[4] == mountPoint);
        var match = mounts.Select(f => AnonymousVolumeRoot().Match(f[3])).FirstOrDefault(m => m.Success);

        return match?.Groups[1].Value;
    }

    public static string FindAnonymousVolumeWarning(string dataDirectory)
    {
        try
        {
            return File.Exists(MountInfoPath) ? FindAnonymousVolumeWarning(dataDirectory, File.ReadAllText(MountInfoPath)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"/volumes/([0-9a-f]{64})/_data$")]
    internal static partial Regex AnonymousVolumeRoot();
}
