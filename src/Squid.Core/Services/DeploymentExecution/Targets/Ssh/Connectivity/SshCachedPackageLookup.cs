using System.IO;
using Serilog;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

/// <summary>
/// Default SSH implementation of <see cref="ICachedPackageLookup"/>.
/// Computes the MD5 of the local package bytes and compares it against
/// <c>md5sum</c> output on the remote host; on match the package is
/// considered already staged.
/// </summary>
public class SshCachedPackageLookup : ICachedPackageLookup
{
    public bool TryFindCachedPackage(ISshConnectionScope scope, string remoteNupkgPath, string localPath)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            var ssh = scope.GetSshClient();

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
}
