using Squid.Core.Services.DeploymentExecution.Packages.Staging;
using Squid.Core.Services.DeploymentExecution.Ssh.Packages;

namespace Squid.Core.Services.DeploymentExecution.Ssh.Packages.Staging;

/// <summary>
/// Highest-priority SSH staging handler: if the remote cache already contains
/// a byte-identical copy of the package, return a <see cref="PackageStagingStrategy.CacheHit"/>
/// plan and skip the upload entirely.
///
/// <para><b>P1-Phase9.6 namespace move</b>: previously lived under
/// <c>Squid.Core.Services.DeploymentExecution.Packages.Staging.Handlers</c>
/// (a generic-looking directory) but the implementation was SSH-specific —
/// it casts to <c>SshPackageStagingContext</c>. New transports adding their own
/// staging implementations should mirror this layout: under
/// <c>Targets/&lt;Transport&gt;/Packages/Staging/</c>. Same structural fix as
/// Phase-8.7's <c>EndpointVariableFactory</c> move.</para>
/// </summary>
public class CacheHitStagingHandler : IPackageStagingHandler
{
    private readonly ICachedPackageLookup _cachedLookup;

    public CacheHitStagingHandler(ICachedPackageLookup cachedLookup)
    {
        _cachedLookup = cachedLookup;
    }

    public int Priority => 100;

    public bool CanHandle(PackageRequirement requirement, PackageStagingContext context)
    {
        return context is SshPackageStagingContext;
    }

    public Task<PackageStagingPlan> TryPlanAsync(PackageRequirement requirement, PackageStagingContext context, CancellationToken ct)
    {
        var sshContext = (SshPackageStagingContext)context;
        var remoteNupkgPath = SshPaths.PackageNupkgPath(sshContext.BaseDirectory, requirement.PackageId, requirement.Version);

        if (!_cachedLookup.TryFindCachedPackage(sshContext.Scope, remoteNupkgPath, requirement.LocalPath))
            return Task.FromResult<PackageStagingPlan>(null);

        var plan = new PackageStagingPlan(
            PackageStagingStrategy.CacheHit,
            requirement.PackageId,
            requirement.Version,
            RemotePath: remoteNupkgPath,
            LocalPath: null,
            Hash: requirement.Hash);

        return Task.FromResult(plan);
    }
}
