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
    public static void ExtractPackage(SftpClient sftp, SshClient ssh, string remoteArchivePath, string extractDir)
    {
        SshFileTransfer.EnsureDirectoryExists(sftp, extractDir);

        var command = BuildExtractCommand(remoteArchivePath, extractDir);
        var result = SshRemoteShellExecutor.Execute(ssh, command, TimeSpan.FromMinutes(5));

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to extract package {remoteArchivePath} to {extractDir}: {result.Error}");

        Log.Information("[SSH] Extracted package {RemotePath} to {ExtractDir}", remoteArchivePath, extractDir);
    }

    internal static string BuildExtractCommand(string remoteArchivePath, string extractDir)
    {
        var lower = (remoteArchivePath ?? string.Empty).ToLowerInvariant();
        if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz") || lower.EndsWith(".tar"))
        {
            var flags = lower.EndsWith(".tar") ? "-xf" : "-xzf";
            return string.Format("cd \"{0}\" && tar {1} \"{2}\"", extractDir, flags, remoteArchivePath);
        }

        return string.Format("cd \"{0}\" && unzip -q -o \"{1}\"", extractDir, remoteArchivePath);
    }
}
