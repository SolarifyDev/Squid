using Squid.Core.Services.Packages.Storage.Exceptions;

namespace Squid.Core.Services.Packages.Storage.Providers.FileSystem;

/// <summary>
/// Refuses to follow a symbolic link or reparse point below the store's root, so a link someone planted inside the folder cannot send a read, write
/// or delete to a file outside it. The root itself may be a link (a mount point is common); only what is beneath it is checked.
/// </summary>
public static class FileSystemLinks
{
    public static void EnsureNoLinks(string root, string fullPath, string key)
    {
        var current = root;

        foreach (var segment in Path.GetRelativePath(root, fullPath).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);

            if (IsLink(current))
                throw new InvalidPackageBlobKeyException(key, $"it passes through a symbolic link ({segment})");
        }
    }

    /// <summary>True for a symbolic link or reparse point at the path, including one whose target is missing. False when nothing is there.</summary>
    public static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);

        return info.LinkTarget != null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }
}
