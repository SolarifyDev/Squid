using System.IO;
using Renci.SshNet;
using Serilog;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public static class SshPackageTransfer
{
    public static void UploadPackageWithCache(SftpClient sftp, SshClient ssh, string localPath, string packageId, string version, string baseDir)
    {
        var remoteNupkgPath = SshPaths.PackageNupkgPath(baseDir, packageId, version);

        if (FindCachedPackage(ssh, remoteNupkgPath, localPath))
        {
            Log.Information("[SSH] Package {PackageId} v{Version} already cached at {RemotePath}, skipping upload", packageId, version, remoteNupkgPath);
            return;
        }

        Log.Information("[SSH] Uploading package {PackageId} v{Version} to {RemotePath}", packageId, version, remoteNupkgPath);

        var localBytes = File.ReadAllBytes(localPath);
        SshFileTransfer.UploadBytesVerified(sftp, ssh, localBytes, remoteNupkgPath);
    }

    private static bool FindCachedPackage(SshClient ssh, string remoteNupkgPath, string localPath)
    {
        try
        {
            var localBytes = File.ReadAllBytes(localPath);
            var localHash = SshFileTransfer.ComputeLocalMd5(localBytes);
            var remoteHash = SshFileTransfer.CalculateRemoteMd5(ssh, remoteNupkgPath);

            if (string.IsNullOrEmpty(remoteHash)) return false;

            if (!string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("[SSH] Cached package hash mismatch (local={LocalHash}, remote={RemoteHash}), re-uploading", localHash, remoteHash);
                return false;
            }

            Log.Debug("[SSH] Cached package hash verified: {Hash}", remoteHash);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[SSH] Cache check failed, proceeding with upload: {Message}", ex.Message);
            return false;
        }
    }

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
