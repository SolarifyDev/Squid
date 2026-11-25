using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.Versioning.Tokens;

/// <summary>
/// <c>{file.version}</c>: the version in the uploaded file's name, in Octopus' <c>PackageId.Version.ext</c> form (<c>Acme.Web.1.2.3-beta.1.zip</c> gives <c>1.2.3-beta.1</c>).
/// The extension is cut off first, so the two-part <c>.tar.gz</c> cannot be mistaken for part of a pre-release tag. The name is split by <see cref="PackageFileName"/>,
/// whose linear-time patterns keep a hostile file name from making the match explode.
/// </summary>
public sealed class FileVersionToken : IVersionTokenSource
{
    public const string TokenName = "file.version";

    public const int MaxFileNameLength = PackageFileName.MaxFileNameLength;

    public string Token => TokenName;

    public Task<string> ResolveAsync(VersionTokenContext context, string argument, CancellationToken ct)
    {
        if (argument != null)
            throw new InvalidVersionTokenArgumentException(TokenName, argument, "this token takes no argument");

        var fileName = ReadFileName(context);

        if (!PackageFileName.TryParse(StripExtension(fileName), out _, out var version))
            throw new VersionTokenValueMissingException(TokenName, $"the file name '{fileName}' has no version; expected PackageId.Version.ext, for example Acme.Web.1.2.3.zip");

        return Task.FromResult(version);
    }

    private static string ReadFileName(VersionTokenContext context)
    {
        var fileName = Path.GetFileName(context.FileName ?? string.Empty);

        if (string.IsNullOrWhiteSpace(fileName))
            throw new VersionTokenValueMissingException(TokenName, "the upload has no file name to read a version from");

        if (fileName.Length > MaxFileNameLength)
            throw new VersionTokenValueMissingException(TokenName, $"the file name is {fileName.Length} characters long, the limit is {MaxFileNameLength}");

        return fileName;
    }

    private static string StripExtension(string fileName)
    {
        return PackageFileName.StripExtension(fileName) ?? throw new VersionTokenValueMissingException(TokenName, $"the file name '{fileName}' does not end in a supported extension ({string.Join(", ", PackageFileName.Extensions)})");
    }
}
