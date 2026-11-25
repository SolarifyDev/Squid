using System.Text.RegularExpressions;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Core.Services.Packages.Versioning;

namespace Squid.Core.Services.Packages.BuiltIn;

/// <summary>Where a pushed package's id comes from and what an id may look like.</summary>
public static partial class BuiltInPackageIds
{
    public const int MaxLength = 255;

    /// <summary>The id the caller gave, else the one in the file name (<c>PackageId.Version.ext</c>); throws when neither is usable.</summary>
    public static string Resolve(string explicitId, string fileName)
    {
        var id = string.IsNullOrWhiteSpace(explicitId) ? ReadFromFileName(fileName) : explicitId.Trim();

        EnsureValid(id);

        return id;
    }

    private static string ReadFromFileName(string fileName)
    {
        // A client may send a whole path, with either separator whatever this server's own is.
        var path = fileName ?? string.Empty;
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        var withoutExtension = PackageFileName.StripExtension(name);

        if (withoutExtension == null || !PackageFileName.TryParse(withoutExtension, out var packageId, out _))
            throw new PackageIdRequiredException(fileName);

        return packageId;
    }

    private static void EnsureValid(string id)
    {
        if (id.Length > MaxLength)
            throw new InvalidPackageIdException(id, $"it is longer than {MaxLength} characters");

        if (!ValidId().IsMatch(id))
            throw new InvalidPackageIdException(id, "it may only contain the ASCII letters and digits and the separators '.', '-' and '_', and must start and end with a letter or digit");
    }

    // ASCII only, so that lowering an id in C# and with lower() in the database always agree, whatever the server culture is (Turkish I). Every id this feed accepts is one the Octopus file-name convention can read back.
    [GeneratedRegex("^[A-Za-z0-9]+([_.-][A-Za-z0-9]+)*$")]
    private static partial Regex ValidId();
}
