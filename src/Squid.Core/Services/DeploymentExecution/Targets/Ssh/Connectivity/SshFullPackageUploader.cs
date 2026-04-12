using System.IO;
using Serilog;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

/// <summary>
/// Default SSH implementation of <see cref="IFullPackageUploader"/>.
/// Reads the local package bytes and uploads them via
/// <see cref="SshFileTransfer.UploadBytesVerified"/>, which also performs a
/// post-upload MD5 check.
/// </summary>
public class SshFullPackageUploader : IFullPackageUploader
{
    public void UploadPackage(ISshConnectionScope scope, string localPath, string remoteNupkgPath)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var sftp = scope.GetSftpClient();
        var ssh = scope.GetSshClient();

        var localBytes = File.ReadAllBytes(localPath);

        Log.Information("[SSH] Uploading package bytes ({Size} bytes) to {RemotePath}", localBytes.Length, remoteNupkgPath);

        SshFileTransfer.UploadBytesVerified(sftp, ssh, localBytes, remoteNupkgPath);
    }
}
