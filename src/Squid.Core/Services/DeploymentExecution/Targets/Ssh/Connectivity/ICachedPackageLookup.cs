using Squid.Core.DependencyInjection;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

/// <summary>
/// Reads the package cache on an SSH target: given a local package file and a
/// prospective remote path, returns true when a byte-identical copy already
/// exists at the remote location (based on MD5 verification).
/// </summary>
public interface ICachedPackageLookup : IScopedDependency
{
    bool TryFindCachedPackage(ISshConnectionScope scope, string remoteNupkgPath, string localPath);
}
