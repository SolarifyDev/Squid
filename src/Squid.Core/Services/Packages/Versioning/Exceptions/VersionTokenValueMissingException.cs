namespace Squid.Core.Services.Packages.Versioning.Exceptions;

public sealed class VersionTokenValueMissingException(string token, string detail) : PackageVersionTemplateException($"Version token '{{{token}}}' has no value: {detail}")
{
    public string Token { get; } = token;
}
