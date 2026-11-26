using System.Text.RegularExpressions;
using Squid.Core.Services.Packages.Storage.Exceptions;

namespace Squid.Core.Services.Packages.Storage.Providers.FileSystem;

/// <summary>
/// Turns a blob key into a path that is certain to be inside the store's root, or refuses the key. The rules are the strictest of every OS, so a key
/// that is accepted here names the same blob on Linux, macOS and Windows, and one store can be moved between them.
/// </summary>
public static partial class FileSystemBlobKey
{
    /// <summary>Where a write is staged before it is moved into place. A key may not start here, so a listing never shows a half-written file.</summary>
    public const string StagingFolderName = ".squid-tmp";

    /// <summary>Where a store test writes its probe file. Keys may use it (the probe does), but a listing never shows it.</summary>
    public const string ProbeFolderName = ".squid-probe";

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

        EnsureSafe(prefix.EndsWith('/') ? prefix[..^1] : prefix, prefix, isPrefix: true);
    }

    public static bool IsStaging(string key) => FirstSegmentIs(key, StagingFolderName);

    /// <summary>True for the store's own bookkeeping (staging files, probe files), which a listing leaves out.</summary>
    public static bool IsHiddenFromListing(string key) => IsStaging(key) || FirstSegmentIs(key, ProbeFolderName);

    /// <summary>
    /// The backstop behind <see cref="EnsureSafe(string)"/>: whether a full path is strictly inside the root (the root itself is not, a sibling that only
    /// shares a name prefix is not). Public so the rule can be tested directly; on Unix no key reaches it after the checks above.
    /// </summary>
    public static bool IsUnder(string root, string fullPath)
    {
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal);
    }

    private static bool FirstSegmentIs(string key, string name) => string.Equals(key.Split('/')[0], name, StringComparison.OrdinalIgnoreCase);

    private static void EnsureSafe(string key) => EnsureSafe(key, key, isPrefix: false);

    private static void EnsureSafe(string key, string reported, bool isPrefix)
    {
        if (string.IsNullOrEmpty(key))
            throw new InvalidPackageBlobKeyException(reported, "it is empty");

        if (key.Contains('\\'))
            throw new InvalidPackageBlobKeyException(reported, "it contains a backslash; keys are separated with '/'");

        if (key.Any(char.IsControl))
            throw new InvalidPackageBlobKeyException(reported, "it contains a control character");

        if (key.Contains(':'))
            throw new InvalidPackageBlobKeyException(reported, "it contains ':', which is not allowed in a file name on every OS");

        if (key.StartsWith('/') || Path.IsPathRooted(key))
            throw new InvalidPackageBlobKeyException(reported, "it is an absolute path; keys are relative");

        EnsureSegmentsSafe(key, reported, isPrefix);
    }

    private static void EnsureSegmentsSafe(string key, string reported, bool isPrefix)
    {
        var segments = key.Split('/');

        if (segments.Any(s => s.Length == 0))
            throw new InvalidPackageBlobKeyException(reported, "it has an empty segment");

        if (segments.Any(s => s is "." or ".."))
            throw new InvalidPackageBlobKeyException(reported, "it has a '.' or '..' segment");

        if (IsStaging(key))
            throw new InvalidPackageBlobKeyException(reported, $"'{StagingFolderName}' is reserved for the store's own use");

        // A prefix may stop part-way through a name, so its last segment is allowed to end in '.' or look like the start of a reserved name.
        foreach (var segment in isPrefix ? segments[..^1] : segments)
            EnsureSegmentPortable(segment, reported);
    }

    private static void EnsureSegmentPortable(string segment, string reported)
    {
        if (segment.EndsWith('.') || segment.EndsWith(' '))
            throw new InvalidPackageBlobKeyException(reported, $"segment \"{segment}\" ends with a dot or a space, which Windows does not keep");

        if (ReservedDeviceName().IsMatch(segment))
            throw new InvalidPackageBlobKeyException(reported, $"segment \"{segment}\" is a reserved device name on Windows");
    }

    // CON, PRN, AUX, NUL, COM1-9, LPT1-9, with or without an extension, in any case.
    [GeneratedRegex(@"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedDeviceName();
}
