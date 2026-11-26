namespace Squid.Core.Services.Packages.Versioning.Exceptions;

public sealed class NonSortablePackageVersionException(string template, string version, string reason) : PackageVersionTemplateException($"Version template '{template}' produced '{version}', which is not a usable version: {reason}")
{
    public string Template { get; } = template;

    public string Version { get; } = version;
}
