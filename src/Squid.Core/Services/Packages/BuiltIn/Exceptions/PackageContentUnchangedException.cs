using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

/// <summary>
/// Not a failure: the push pipeline's way out when the latest version already holds the pushed bytes. The push service catches it and answers with
/// <see cref="Existing"/>, so nothing is stored and no version number is taken.
/// </summary>
public sealed class PackageContentUnchangedException(BuiltInPackage existing) : InvalidOperationException($"Version {existing.Version} of package '{existing.PackageId}' already holds this exact content")
{
    public BuiltInPackage Existing { get; } = existing;
}
