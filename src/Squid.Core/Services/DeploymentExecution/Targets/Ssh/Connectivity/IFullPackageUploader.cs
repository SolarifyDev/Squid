using Squid.Core.DependencyInjection;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

/// <summary>
/// Uploads the full bytes of a package to an SSH target at the prospective
/// remote path. Implementations also verify the post-upload MD5 matches the
/// local one.
/// </summary>
public interface IFullPackageUploader : IScopedDependency
{
    void UploadPackage(ISshConnectionScope scope, string localPath, string remoteNupkgPath);
}
