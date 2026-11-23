using Serilog;

namespace Squid.Tentacle.ScriptExecution;

/// <summary>
/// Finds the mounted volume that actually holds a path. <c>Path.GetPathRoot</c> is
/// always <c>/</c> on Linux/macOS, so measuring "the root of the path" silently
/// measures the root filesystem instead of the PVC / tmpfs / partition the path lives on.
/// Cost: <c>DriveInfo.GetDrives()</c> is a single mount-table read (a few microseconds on
/// macOS, one <c>/proc/self/mountinfo</c> parse on Linux), so no cache is kept — a cache
/// would only add staleness when a volume is mounted or unmounted at runtime.
/// </summary>
internal static class VolumeResolver
{
    private const int MaxSymlinkHops = 40;

    internal static string? ResolveVolumeRoot(string path)
    {
        var mountRoots = DriveInfo.GetDrives().Select(d => d.Name).ToList();

        if (mountRoots.Count == 0) return null;

        return PickReadyMountRoot(ResolveRealPath(path), mountRoots, Path.DirectorySeparatorChar, PathComparison, IsReady);
    }

    /// <summary>Pure: the mount root with the longest component-wise prefix match for the path, or null.</summary>
    internal static string? PickMountRoot(string path, IEnumerable<string> mountRoots, char separator, StringComparison comparison)
    {
        return PickReadyMountRoot(path, mountRoots, separator, comparison, _ => true);
    }

    /// <summary>
    /// Matches on mount NAMES only, then probes readiness best-candidate-first and stops at the first ready one.
    /// A readiness probe is a statfs; a hung NFS/CIFS/FUSE mount elsewhere on the host must never be touched.
    /// </summary>
    internal static string? PickReadyMountRoot(string path, IEnumerable<string> mountRoots, char separator, StringComparison comparison, Func<string, bool> isReady)
    {
        var target = NormalizeRoot(path, separator);

        var candidates = mountRoots
            .Select(root => (Root: root, Normalized: NormalizeRoot(root, separator)))
            .Where(c => IsPathWithin(target, c.Normalized, separator, comparison))
            .OrderByDescending(c => c.Normalized.Length);

        foreach (var candidate in candidates)
        {
            if (isReady(candidate.Root)) return candidate.Root;
        }

        return null;
    }

    private static bool IsPathWithin(string path, string root, char separator, StringComparison comparison)
    {
        if (!path.StartsWith(root, comparison)) return false;

        if (path.Length == root.Length || root[^1] == separator) return true;

        return path[root.Length] == separator;
    }

    private static string NormalizeRoot(string path, char separator)
    {
        var trimmed = path.TrimEnd(separator);

        if (trimmed.Length == 0) return separator.ToString();

        return trimmed[^1] == ':' ? trimmed + separator : trimmed;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsReady(string mountRoot)
    {
        try
        {
            return new DriveInfo(mountRoot).IsReady;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Skipping mount {Mount}: readiness probe failed", mountRoot);
            return false;
        }
    }

    // A path that does not exist yet has no symlinks to follow in its missing tail, so the textual
    // prefix match on the resolved existing part already lands on the nearest existing ancestor's volume.
    private static string ResolveRealPath(string path) => ResolveSymlinks(Path.GetFullPath(path));

    private static string ResolveSymlinks(string fullPath)
    {
        var current = fullPath;

        for (var hop = 0; hop < MaxSymlinkHops; hop++)
        {
            var next = ExpandFirstSymlink(current);

            if (next == null) return current;

            current = next;
        }

        return current;
    }

    private static string? ExpandFirstSymlink(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var prefix = root;

        foreach (var part in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(prefix, part);
            var target = TryReadLinkTarget(next);

            if (target != null) return CombineRemainder(prefix, target, fullPath, next);

            prefix = next;
        }

        return null;
    }

    private static string CombineRemainder(string parent, string linkTarget, string fullPath, string linkPath)
    {
        var resolved = Path.GetFullPath(linkTarget, parent);
        var remainder = fullPath[linkPath.Length..].TrimStart(Path.DirectorySeparatorChar);

        return remainder.Length == 0 ? resolved : Path.Combine(resolved, remainder);
    }

    private static string? TryReadLinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
