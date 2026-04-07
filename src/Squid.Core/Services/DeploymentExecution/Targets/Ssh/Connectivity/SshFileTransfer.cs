using Renci.SshNet;
using Serilog;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public static class SshFileTransfer
{
    public static void UploadBytes(SftpClient client, byte[] data, string remotePath)
    {
        EnsureParentDirectoryExists(client, remotePath);

        using var stream = new MemoryStream(data);
        client.UploadFile(stream, remotePath, canOverride: true);

        Log.Debug("[SSH] Uploaded {Bytes} bytes to {RemotePath}", data.Length, remotePath);
    }

    public static void UploadFile(SftpClient client, Stream source, string remotePath)
    {
        EnsureParentDirectoryExists(client, remotePath);

        client.UploadFile(source, remotePath, canOverride: true);

        Log.Debug("[SSH] Uploaded file to {RemotePath}", remotePath);
    }

    public static byte[] DownloadFile(SftpClient client, string remotePath)
    {
        using var stream = new MemoryStream();
        client.DownloadFile(remotePath, stream);

        return stream.ToArray();
    }

    public static void EnsureDirectoryExists(SftpClient client, string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath)) return;

        var parts = remotePath.Split('/');
        var current = string.Empty;

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            current = string.IsNullOrEmpty(current) ? part : $"{current}/{part}";

            if (!DirectoryExists(client, current))
            {
                client.CreateDirectory(current);
                Log.Debug("[SSH] Created remote directory: {Path}", current);
            }
        }
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
        catch
        {
            return false;
        }
    }
}
