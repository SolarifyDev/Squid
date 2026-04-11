using Renci.SshNet;
using Serilog;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

/// <summary>
/// Static helpers for post-staging package operations on SSH targets.
/// The cache-lookup and upload responsibilities that previously lived here
/// have been split into <see cref="ICachedPackageLookup"/> and
/// <see cref="IFullPackageUploader"/>, which are consumed by the
/// <c>IPackageStagingPlanner</c> handler chain.
/// </summary>
public static class SshPackageTransfer
{
    public static void ExtractPackage(SftpClient sftp, SshClient ssh, string remoteNupkgPath, string extractDir)
    {
        SshFileTransfer.EnsureDirectoryExists(sftp, extractDir);

        var command = $"cd \"{extractDir}\" && unzip -q -o \"{remoteNupkgPath}\"";
        var result = SshRemoteShellExecutor.Execute(ssh, command, TimeSpan.FromMinutes(5));

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to extract package {remoteNupkgPath} to {extractDir}: {result.Error}");

        Log.Information("[SSH] Extracted package {RemotePath} to {ExtractDir}", remoteNupkgPath, extractDir);
    }
}
