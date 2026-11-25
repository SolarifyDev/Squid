using Squid.Core.Services.Packages.Storage.Exceptions;

namespace Squid.Core.Services.Packages.Storage.Providers.FileSystem;

/// <summary>Turns a blob key into a path that is certain to be inside the store's root, or refuses the key.</summary>
public static class FileSystemBlobKey
{
    /// <summary>Where a write is staged before it is moved into place. A key may not start here, so a listing never shows a half-written file.</summary>
    public const string StagingFolderName = ".squid-tmp";

    public static string ToFullPath(string root, string key)
    {
        EnsureSafe(key);

        var fullPath = Path.GetFullPath(Path.Combine(root, key));

        if (!IsUnder(root, fullPath))
            throw new InvalidPackageBlobKeyException(key, "it resolves outside the store's root folder");

        return fullPath;
    }

    /// <summary>A prefix is a key that may stop part-way through a name or end with '/', and may be empty (everything).</summary>
    public static void EnsurePrefixSafe(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return;

        EnsureSafe(prefix.EndsWith('/') ? prefix[..^1] : prefix, prefix);
    }

    public static bool IsStaging(string key) => key.Split('/')[0] == StagingFolderName;

    private static void EnsureSafe(string key) => EnsureSafe(key, key);

    private static void EnsureSafe(string key, string reported)
    {
        if (string.IsNullOrEmpty(key))
            throw new InvalidPackageBlobKeyException(reported, "it is empty");

        if (key.Contains('\0'))
            throw new InvalidPackageBlobKeyException(reported, "it contains a NUL character");

        if (key.Contains('\\'))
            throw new InvalidPackageBlobKeyException(reported, "it contains a backslash; keys are separated with '/'");

        if (key.StartsWith('/') || Path.IsPathRooted(key))
            throw new InvalidPackageBlobKeyException(reported, "it is an absolute path; keys are relative");

        EnsureSegmentsSafe(key, reported);
    }

    private static void EnsureSegmentsSafe(string key, string reported)
    {
        var segments = key.Split('/');

        if (segments.Any(s => s.Length == 0))
            throw new InvalidPackageBlobKeyException(reported, "it has an empty segment");

        if (segments.Any(s => s is "." or ".."))
            throw new InvalidPackageBlobKeyException(reported, "it has a '.' or '..' segment");

        if (segments[0] == StagingFolderName)
            throw new InvalidPackageBlobKeyException(reported, $"'{StagingFolderName}' is reserved for the store's own use");
    }

    private static bool IsUnder(string root, string fullPath)
    {
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal);
    }
}
