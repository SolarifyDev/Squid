namespace Squid.Core.Services.Packages.Versioning.Exceptions;

public sealed class InvalidVersionTokenArgumentException(string token, string argument, string detail) : PackageVersionTemplateException($"Version token '{{{token}}}' cannot take the argument '{argument}': {detail}")
{
    public string Token { get; } = token;

    public string Argument { get; } = argument;
}
