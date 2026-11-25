using System.Text.RegularExpressions;

namespace Squid.Core.Services.Packages.Versioning;

/// <summary>Reads a package file name in Octopus' <c>PackageId.Version.ext</c> form (<c>Acme.Web.1.2.3-beta.1.zip</c>).</summary>
public static partial class PackageFileName
{
    // Longest first, so the two-part ".tar.gz" is cut as a whole and cannot be mistaken for part of a pre-release tag.
    public static readonly IReadOnlyList<string> Extensions = [".tar.gz", ".tgz", ".nupkg", ".zip", ".tar"];

    /// <summary>The name without its package extension; null when it does not end in one of <see cref="Extensions"/>.</summary>
    public static string StripExtension(string fileName)
    {
        var extension = Extensions.FirstOrDefault(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        return extension == null ? null : fileName[..^extension.Length];
    }

    /// <summary>Splits a name that has no extension left into its package id and version; false when it has no version.</summary>
    public static bool TryParse(string nameWithoutExtension, out string packageId, out string version)
    {
        var match = NameAndVersion().Match(nameWithoutExtension);

        packageId = match.Success ? match.Groups["packageId"].Value : null;
        version = match.Success ? match.Groups["version"].Value : null;

        return match.Success;
    }

    // Octopus' package id and version patterns (PackageName.TryParseUnsafeFileName).
    [GeneratedRegex(@"^(?<packageId>\w+([_.-]\w+)*?)\.(?<version>\d+(\.\d+){0,3}(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex NameAndVersion();
}
