using System.Text.RegularExpressions;

namespace Squid.Core.Services.Packages.Versioning;

/// <summary>
/// Reads a package file name in Octopus' <c>PackageId.Version.ext</c> form (<c>Acme.Web.1.2.3-beta.1.zip</c>). The one implementation, used by the
/// <c>{file.version}</c> token and by the built-in feed's package id reading. The package id and the version are matched by two separate linear-time patterns at each
/// dot, earliest split first, so a hostile file name cannot make the match explode.
/// </summary>
public static class PackageFileName
{
    public const int MaxFileNameLength = 200;

    // Longest first, so the two-part ".tar.gz" is cut as a whole and cannot be mistaken for part of a pre-release tag.
    public static readonly IReadOnlyList<string> Extensions = [".tar.gz", ".tgz", ".nupkg", ".zip", ".tar"];

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    // Separators are not word characters, so each package id has exactly one way to match.
    private static readonly Regex PackageIdPattern = new(@"^\w+([.\-]\w+)*$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex VersionPattern = new(@"^\d+(\.\d+){0,3}(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>The name without its package extension; null when it does not end in one of <see cref="Extensions"/>.</summary>
    public static string StripExtension(string fileName)
    {
        var extension = Extensions.FirstOrDefault(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        return extension == null ? null : fileName[..^extension.Length];
    }

    /// <summary>
    /// Reads a whole upload file name (a client may send a path, with either separator whatever this server's own is) into its package id and version; false when it
    /// does not end in a package extension or has no id and version in it.
    /// </summary>
    public static bool TryParseFileName(string fileName, out string packageId, out string version)
    {
        packageId = null;
        version = null;

        var path = fileName ?? string.Empty;
        var withoutExtension = StripExtension(path[(path.LastIndexOfAny(['/', '\\']) + 1)..]);

        return withoutExtension != null && TryParse(withoutExtension, out packageId, out version);
    }

    /// <summary>
    /// Splits a name that has no extension left into its package id and version, at the earliest dot that has a valid package id before it and a valid version after it
    /// (as the lazy package id of Octopus' <c>PackageName.TryParseUnsafeFileName</c> would pick); false when there is none, or the name is over <see cref="MaxFileNameLength"/>.
    /// </summary>
    public static bool TryParse(string nameWithoutExtension, out string packageId, out string version)
    {
        packageId = null;
        version = null;

        if (nameWithoutExtension == null || nameWithoutExtension.Length < 2 || nameWithoutExtension.Length > MaxFileNameLength) return false;

        for (var dot = nameWithoutExtension.IndexOf('.', 1); dot > 0; dot = nameWithoutExtension.IndexOf('.', dot + 1))
        {
            if (!PackageIdPattern.IsMatch(nameWithoutExtension.AsSpan(0, dot)) || !VersionPattern.IsMatch(nameWithoutExtension.AsSpan(dot + 1))) continue;

            packageId = nameWithoutExtension[..dot];
            version = nameWithoutExtension[(dot + 1)..];

            return true;
        }

        return false;
    }
}
