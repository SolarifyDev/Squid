using Squid.Core.Setup;

namespace Squid.Core.Services.Packages.Storage.Providers.FileSystem;

/// <summary>
/// Best-effort check that a store's root folder will still be there after the container is recreated, from the text of <c>/proc/self/mountinfo</c>
/// (the approach of <see cref="DataVolumeInspector"/>). Returns a warning only when it can tell the folder is on the container's own filesystem or
/// in an anonymous Docker volume; not Linux, no mountinfo, or any other mount (named volume, bind mount, host disk) gives none.
/// </summary>
public static class FileSystemRootPersistenceInspector
{
    private const string OverlayType = "overlay";

    public static string FindWarning(string root, string mountInfo)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrEmpty(mountInfo)) return null;

        var mount = FindMountHolding(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), mountInfo);

        if (mount == null) return null;

        if (mount.IsContainerRoot) return OverlayWarning(root);

        return AnonymousVolumeWarning(root, mount);
    }

    public static string FindWarning(string root)
    {
        try
        {
            return File.Exists(DataVolumeInspector.MountInfoPath) ? FindWarning(root, File.ReadAllText(DataVolumeInspector.MountInfoPath)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The deepest mount that contains the folder. mountinfo fields: [3] = root of the mount in its filesystem, [4] = mount point, then optional fields up to "-", then the filesystem type.</summary>
    private static MountEntry FindMountHolding(string root, string mountInfo)
    {
        return mountInfo.Split('\n')
            .Select(ParseLine)
            .Reverse()
            .Where(m => m != null && Contains(m.MountPoint, root))
            .OrderByDescending(m => m.MountPoint.Length)
            .FirstOrDefault();
    }

    private static MountEntry ParseLine(string line)
    {
        var fields = line.Split(' ');
        var separator = Array.IndexOf(fields, "-");

        if (fields.Length < 7 || separator < 6 || separator + 1 >= fields.Length) return null;

        return new MountEntry(Unescape(fields[4]), fields[3], fields[separator + 1]);
    }

    // mountinfo writes a space in a path as \040.
    private static string Unescape(string path) => path.Replace("\\040", " ");

    private static bool Contains(string mountPoint, string path)
    {
        return mountPoint == "/" || path == mountPoint || path.StartsWith(mountPoint + "/", StringComparison.Ordinal);
    }

    private static string OverlayWarning(string root)
    {
        return $"{root} is on the container's own filesystem, so everything stored there is lost when the container is recreated, for example by an upgrade. Mount a volume or a host folder there, e.g. -v squid-packages:{root}, or point this store at a folder that is mounted.";
    }

    private static string AnonymousVolumeWarning(string root, MountEntry mount)
    {
        var match = DataVolumeInspector.AnonymousVolumeRoot().Match(mount.Root);

        if (!match.Success) return null;

        return $"{root} is stored in an anonymous Docker volume ({match.Groups[1].Value[..12]}...). It survives restarts, but a new container created for an upgrade will not see it. Start the container with a named volume, e.g. -v squid-data:{mount.MountPoint}, or upgrade with --volumes-from <old container>.";
    }

    private sealed record MountEntry(string MountPoint, string Root, string FilesystemType)
    {
        public bool IsContainerRoot => MountPoint == "/" && FilesystemType == OverlayType;
    }
}
