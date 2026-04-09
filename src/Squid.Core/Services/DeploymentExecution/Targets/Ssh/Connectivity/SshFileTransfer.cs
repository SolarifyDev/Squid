using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;
using Serilog;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public static class SshFileTransfer
{
    public static void UploadBytes(SftpClient client, byte[] data, string remotePath)
    {
        SshRetryHelper.ExecuteWithRetry(() =>
        {
            EnsureParentDirectoryExists(client, remotePath);

            using var stream = new MemoryStream(data);
            client.UploadFile(stream, remotePath, canOverride: true);

            Log.Debug("[SSH] Uploaded {Bytes} bytes to {RemotePath}", data.Length, remotePath);
        }, SshTransientErrorDetector.IsTransient);
    }

    public static void UploadFile(SftpClient client, Stream source, string remotePath)
    {
        SshRetryHelper.ExecuteWithRetry(() =>
        {
            EnsureParentDirectoryExists(client, remotePath);

            client.UploadFile(source, remotePath, canOverride: true);

            Log.Debug("[SSH] Uploaded file to {RemotePath}", remotePath);
        }, SshTransientErrorDetector.IsTransient);
    }

    public static void UploadBytesVerified(SftpClient sftp, SshClient ssh, byte[] data, string remotePath)
    {
        UploadBytes(sftp, data, remotePath);

        var localHash = ComputeLocalMd5(data);
        var remoteHash = CalculateRemoteMd5(ssh, remotePath);

        if (!string.IsNullOrEmpty(remoteHash) && !string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"MD5 checksum mismatch for {remotePath}: local={localHash}, remote={remoteHash}");
    }

    public static byte[] DownloadFile(SftpClient client, string remotePath)
    {
        using var stream = new MemoryStream();
        client.DownloadFile(remotePath, stream);

        return stream.ToArray();
    }

    public static string CalculateRemoteMd5(SshClient ssh, string remotePath)
    {
        try
        {
            var result = SshRemoteShellExecutor.Execute(ssh, $"md5sum \"{remotePath}\" | awk '{{ print $1 }}'", TimeSpan.FromSeconds(10));

            if (result.ExitCode != 0) return string.Empty;

            return result.Output.Trim();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SSH] Failed to calculate remote MD5 for {RemotePath}", remotePath);
            return string.Empty;
        }
    }

    internal static string ComputeLocalMd5(byte[] data)
    {
        var hashBytes = MD5.HashData(data);

        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public static void EnsureDirectoryExists(SftpClient client, string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath)) return;

        foreach (var path in GetDirectoryCreationPaths(remotePath))
        {
            if (!DirectoryExists(client, path))
            {
                client.CreateDirectory(path);
                Log.Debug("[SSH] Created remote directory: {Path}", path);
            }
        }
    }

    internal static IReadOnlyList<string> GetDirectoryCreationPaths(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
            return Array.Empty<string>();

        var normalizedPath = remotePath.Replace('\\', '/');
        var isAbsolute = normalizedPath.StartsWith("/", StringComparison.Ordinal);
        var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return Array.Empty<string>();

        var paths = new List<string>(parts.Length);
        var current = isAbsolute ? "/" : string.Empty;

        foreach (var part in parts)
        {
            current = current switch
            {
                "/" => $"/{part}",
                "" => part,
                _ => $"{current}/{part}"
            };

            paths.Add(current);
        }

        return paths;
    }

    private static void EnsureParentDirectoryExists(SftpClient client, string remotePath)
    {
        var lastSlash = remotePath.LastIndexOf('/');
        if (lastSlash <= 0) return;

        var parentDir = remotePath[..lastSlash];
        EnsureDirectoryExists(client, parentDir);
    }

    private static bool DirectoryExists(SftpClient client, string path)
    {
        try
        {
            var attrs = client.GetAttributes(path);
            return attrs.IsDirectory;
        }
        catch (SftpPathNotFoundException)
        {
            return false;
        }
    }
}
