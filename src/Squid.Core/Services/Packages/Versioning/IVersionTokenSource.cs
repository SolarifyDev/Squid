using Squid.Message.Models.PackageVersioning;

namespace Squid.Core.Services.Packages.Versioning;

/// <summary>Resolves one <c>{token[:argument]}</c> of a version template. Every implementation is picked up by <see cref="IPackageVersionAssigner"/> through the container.</summary>
public interface IVersionTokenSource : IScopedDependency
{
    /// <summary>The token name as written in a template, e.g. <c>counter</c>. Matched ignoring case.</summary>
    string Token { get; }

    /// <summary>The text the token stands for. <paramref name="argument"/> is null when the template gave none. Throws a <c>PackageVersionTemplateException</c> when it cannot.</summary>
    Task<string> ResolveAsync(VersionTokenContext context, string argument, CancellationToken ct);
}
