using Squid.Core.Services.DeploymentExecution.Ssh;
using Squid.Core.Services.DeploymentExecution.Ssh.Packages;

namespace Squid.Core.Services.DeploymentExecution.Packages.Staging.Handlers;

/// <summary>
/// Terminal SSH staging handler: always uploads the package bytes to the
/// target and produces a <see cref="PackageStagingStrategy.FullUpload"/>
/// plan. Runs last in the priority chain after <see cref="CacheHitStagingHandler"/>
/// and any future delta/remote-download handlers.
/// </summary>
public class FullUploadStagingHandler : IPackageStagingHandler
{
    private readonly IFullPackageUploader _uploader;

    public FullUploadStagingHandler(IFullPackageUploader uploader)
    {
        _uploader = uploader;
    }

    public int Priority => 10;

    public bool CanHandle(PackageRequirement requirement, PackageStagingContext context)
    {
        return context is SshPackageStagingContext;
    }

    public Task<PackageStagingPlan> TryPlanAsync(PackageRequirement requirement, PackageStagingContext context, CancellationToken ct)
    {
        var sshContext = (SshPackageStagingContext)context;
        var remoteNupkgPath = SshPaths.PackageNupkgPath(sshContext.BaseDirectory, requirement.PackageId, requirement.Version);

        _uploader.UploadPackage(sshContext.Scope, requirement.LocalPath, remoteNupkgPath);

        var plan = new PackageStagingPlan(
            PackageStagingStrategy.FullUpload,
            requirement.PackageId,
            requirement.Version,
            RemotePath: remoteNupkgPath,
            LocalPath: requirement.LocalPath,
            Hash: requirement.Hash);

        return Task.FromResult(plan);
    }
}
