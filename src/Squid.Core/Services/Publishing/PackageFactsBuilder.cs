using Squid.Core.Services.Packages.BuiltIn;
using Squid.Message.Models.Publishing;

namespace Squid.Core.Services.Publishing;

/// <summary>Turns an archive's file listing into the starting facts: the paths, whether the listing is truncated, and the effective root.</summary>
public static class PackageFactsBuilder
{
    // macOS's zip puts a resource-fork copy of every file under this folder, next to the real one; it must not make a wrapped archive look like two folders.
    private const string MacResourceForkFolder = "__MACOSX/";

    public static PackageFacts FromListing(PackageArchiveListing listing)
    {
        var files = listing.Entries.Select(e => e.Path).Where(IsContent).ToList();
        var facts = new PackageFacts { Files = files };
        var root = DetectRoot(files);

        facts.Values[PackageFactKeys.ArchiveWrapped] = root.Length > 0 ? PackageFactKeys.True : "false";
        facts.Values[PackageFactKeys.ArchiveRoot] = root;

        facts.Values[PackageFactKeys.FilesCount] = files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (listing.Truncated) facts.Values[PackageFactKeys.FilesTruncated] = PackageFactKeys.True;

        return facts;
    }

    /// <summary>The single top-level folder every file sits under, or empty when there is none (a file at the top, or two or more folders).</summary>
    public static string DetectRoot(IReadOnlyList<string> files)
    {
        if (files.Count == 0 || files.Any(f => !f.Contains('/'))) return string.Empty;

        var first = files[0][..files[0].IndexOf('/')];

        return files.All(f => f.StartsWith(first + "/", StringComparison.Ordinal)) ? first : string.Empty;
    }

    private static bool IsContent(string path) => !path.StartsWith(MacResourceForkFolder, StringComparison.Ordinal);
}
