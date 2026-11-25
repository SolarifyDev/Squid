using System.Text.RegularExpressions;
using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.Versioning.Tokens;

/// <summary>
/// <c>{file.version}</c>: the version in the uploaded file's name, in Octopus' <c>PackageId.Version.ext</c> form (<c>Acme.Web.1.2.3-beta.1.zip</c> gives <c>1.2.3-beta.1</c>).
/// The extension is cut off first, so the two-part <c>.tar.gz</c> cannot be mistaken for part of a pre-release tag.
/// </summary>
public sealed partial class FileVersionToken : IVersionTokenSource
{
    public const string TokenName = "file.version";

    private static readonly string[] Extensions = [".tar.gz", ".tgz", ".nupkg", ".zip", ".tar"];

    public string Token => TokenName;

    public Task<string> ResolveAsync(VersionTokenContext context, string argument, CancellationToken ct)
    {
        if (argument != null)
            throw new InvalidVersionTokenArgumentException(TokenName, argument, "this token takes no argument");

        var fileName = ReadFileName(context);
        var withoutExtension = StripExtension(fileName);
        var match = NameAndVersion().Match(withoutExtension);

        if (!match.Success)
            throw new VersionTokenValueMissingException(TokenName, $"the file name '{fileName}' has no version; expected PackageId.Version.ext, for example Acme.Web.1.2.3.zip");

        return Task.FromResult(match.Groups["version"].Value);
    }

    private static string ReadFileName(VersionTokenContext context)
    {
        var fileName = Path.GetFileName(context.FileName ?? string.Empty);

        if (string.IsNullOrWhiteSpace(fileName))
            throw new VersionTokenValueMissingException(TokenName, "the upload has no file name to read a version from");

        return fileName;
    }

    private static string StripExtension(string fileName)
    {
        var extension = Extensions.FirstOrDefault(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        if (extension == null)
            throw new VersionTokenValueMissingException(TokenName, $"the file name '{fileName}' does not end in a supported extension ({string.Join(", ", Extensions)})");

        return fileName[..^extension.Length];
    }

    // Octopus' package id and version patterns (PackageName.TryParseUnsafeFileName).
    [GeneratedRegex(@"^(?<packageId>\w+([_.-]\w+)*?)\.(?<version>\d+(\.\d+){0,3}(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex NameAndVersion();
}
