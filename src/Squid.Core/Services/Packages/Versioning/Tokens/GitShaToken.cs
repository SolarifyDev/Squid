using System.Globalization;
using System.Text.RegularExpressions;
using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.Versioning.Tokens;

/// <summary><c>{git.sha}</c> or <c>{git.sha:length}</c>: the first <c>length</c> (default 7) characters of the commit id the caller supplied.</summary>
public sealed partial class GitShaToken : IVersionTokenSource
{
    public const string TokenName = "git.sha";

    public const int DefaultLength = 7;

    public string Token => TokenName;

    public Task<string> ResolveAsync(VersionTokenContext context, string argument, CancellationToken ct)
    {
        var length = ParseLength(argument);
        var sha = ReadSha(context);

        if (sha.Length < length)
            throw new VersionTokenValueMissingException(TokenName, $"the commit id '{sha}' is only {sha.Length} characters long, so {length} cannot be taken from it");

        return Task.FromResult(sha[..length]);
    }

    private static int ParseLength(string argument)
    {
        if (argument == null) return DefaultLength;

        if (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length is < 1 or > 64)
            throw new InvalidVersionTokenArgumentException(TokenName, argument, "the length must be a whole number from 1 to 64");

        return length;
    }

    private static string ReadSha(VersionTokenContext context)
    {
        var sha = context.GitSha?.Trim();

        if (string.IsNullOrEmpty(sha))
            throw new VersionTokenValueMissingException(TokenName, "no git commit id was supplied with the upload");

        if (!HexCharacters().IsMatch(sha))
            throw new VersionTokenValueMissingException(TokenName, $"'{sha}' is not a git commit id (hexadecimal characters only)");

        return sha.ToLowerInvariant();
    }

    [GeneratedRegex("^[0-9a-fA-F]+$")]
    private static partial Regex HexCharacters();
}
