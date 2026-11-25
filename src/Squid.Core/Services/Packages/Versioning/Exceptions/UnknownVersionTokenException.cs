namespace Squid.Core.Services.Packages.Versioning.Exceptions;

public sealed class UnknownVersionTokenException(string token, IEnumerable<string> knownTokens) : PackageVersionTemplateException($"Version token '{{{token}}}' is not known. Known tokens: {string.Join(", ", knownTokens.OrderBy(t => t, StringComparer.Ordinal))}")
{
    public string Token { get; } = token;
}
