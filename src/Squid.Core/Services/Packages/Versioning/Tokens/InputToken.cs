using Squid.Core.Services.Packages.Versioning.Exceptions;
using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.Versioning.Tokens;

/// <summary><c>{input}</c>: the version the user typed.</summary>
public sealed class InputToken : IVersionTokenSource
{
    public const string TokenName = "input";

    public string Token => TokenName;

    public Task<string> ResolveAsync(VersionTokenContext context, string argument, CancellationToken ct)
    {
        if (argument != null)
            throw new InvalidVersionTokenArgumentException(TokenName, argument, "this token takes no argument");

        if (string.IsNullOrWhiteSpace(context.InputVersion))
            throw new VersionTokenValueMissingException(TokenName, "no version was entered; type a version or choose another version rule");

        return Task.FromResult(context.InputVersion.Trim());
    }
}
