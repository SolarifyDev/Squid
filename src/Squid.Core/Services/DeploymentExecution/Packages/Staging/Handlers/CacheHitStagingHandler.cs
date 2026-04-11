using Squid.Core.Services.DeploymentExecution.Ssh;
using Squid.Core.Services.DeploymentExecution.Ssh.Packages;

namespace Squid.Core.Services.DeploymentExecution.Packages.Staging.Handlers;

/// <summary>
/// Highest-priority SSH staging handler: if the remote cache already contains
/// a byte-identical copy of the package, return a <see cref="PackageStagingStrategy.CacheHit"/>
/// plan and skip the upload entirely. Currently SSH-only — other transports
/// will match via their own derived <see cref="PackageStagingContext"/> types.
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
