using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.Versioning;

/// <remarks>
/// Counters are keyed by the package id as given (trimmed, case-sensitive), so callers must pass the canonical package id of the package, the one the feed stores it
/// under. <c>{date}</c> is the UTC date, read once per call. The built-in feed upload uses <see cref="AssignAsync"/> to version every uploaded package.
/// </remarks>
public interface IPackageVersionAssigner : IScopedDependency
{
    /// <summary>
    /// Renders <paramref name="template"/> into the version of an upload. The whole template is checked first without consuming anything, so a template or a
    /// value that cannot yield a valid version fails before a counter number is taken. Throws a <c>PackageVersionTemplateException</c> saying what is wrong.
    /// </summary>
    Task<string> AssignAsync(string template, VersionTokenContext context, CancellationToken ct);

    /// <summary>Checks <paramref name="template"/> against <paramref name="sample"/> without consuming a counter; returns the version it would give with the first counter value.</summary>
    Task<string> PreviewAsync(string template, VersionTokenContext sample, CancellationToken ct);
}
