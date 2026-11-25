using System.Text.RegularExpressions;
using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.Versioning.Tokens;

/// <summary>
/// <c>{file.version}</c>: the version in the uploaded file's name, in Octopus' <c>PackageId.Version.ext</c> form (<c>Acme.Web.1.2.3-beta.1.zip</c> gives <c>1.2.3-beta.1</c>).
/// The extension is cut off first, so the two-part <c>.tar.gz</c> cannot be mistaken for part of a pre-release tag. The package id and the version are matched by two
/// separate linear-time patterns at each dot, earliest split first, so a hostile file name cannot make the match explode.
/// </summary>
public sealed class FileVersionToken : IVersionTokenSource
{
    public const string TokenName = "file.version";

    public const int MaxFileNameLength = 200;

    private static readonly string[] Extensions = [".tar.gz", ".tgz", ".nupkg", ".zip", ".tar"];

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    // Separators are not word characters, so each package id has exactly one way to match.
    private static readonly Regex PackageIdPattern = new(@"^\w+([.\-]\w+)*$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex VersionPattern = new(@"^\d+(\.\d+){0,3}(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, MatchTimeout);

    public string Token => TokenName;

    public Task<string> ResolveAsync(VersionTokenContext context, string argument, CancellationToken ct)
    {
        if (argument != null)
            throw new InvalidVersionTokenArgumentException(TokenName, argument, "this token takes no argument");

        var fileName = ReadFileName(context);
        var version = FindVersion(StripExtension(fileName));

        return Task.FromResult(version ?? throw new VersionTokenValueMissingException(TokenName, $"the file name '{fileName}' has no version; expected PackageId.Version.ext, for example Acme.Web.1.2.3.zip"));
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
        var extension = Extensions.FirstOrDefault(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        if (extension == null)
            throw new VersionTokenValueMissingException(TokenName, $"the file name '{fileName}' does not end in a supported extension ({string.Join(", ", Extensions)})");

        return fileName[..^extension.Length];
    }

    // The earliest dot that has a valid package id before it and a valid version after it, as the lazy package id of Octopus' PackageName.TryParseUnsafeFileName would pick.
    private static string FindVersion(string name)
    {
        if (name.Length < 2) return null;

        for (var dot = name.IndexOf('.', 1); dot > 0; dot = name.IndexOf('.', dot + 1))
        {
            if (PackageIdPattern.IsMatch(name.AsSpan(0, dot)) && VersionPattern.IsMatch(name.AsSpan(dot + 1)))
                return name[(dot + 1)..];
        }

        return null;
    }
}
