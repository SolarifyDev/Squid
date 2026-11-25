namespace Squid.Core.Services.Packages.Versioning.Exceptions;

public sealed class MalformedVersionTemplateException(string template, string reason) : PackageVersionTemplateException($"Version template '{template}' is malformed: {reason}")
{
    public string Template { get; } = template;
}
