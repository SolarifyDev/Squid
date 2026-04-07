using System.Net.Sockets;
using Renci.SshNet.Common;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public static class SshTransientErrorDetector
{
    private static readonly string[] TransientMessages =
    {
        "Client not connected",
        "Channel was closed",
        "forcibly closed by the remote host",
        "connected party did not properly respond"
    };

    public static bool IsTransient(Exception ex)
    {
        if (ex is SshAuthenticationException) return false;
        if (ex is SftpPermissionDeniedException) return false;

        if (ex is SshConnectionException) return true;
        if (ex is SocketException) return true;
        if (ex is IOException) return true;

        return ContainsTransientMessage(ex);
    }

    private static bool ContainsTransientMessage(Exception ex)
    {
        var message = ex.Message;
        if (string.IsNullOrEmpty(message)) return false;

        foreach (var pattern in TransientMessages)
        {
            if (message.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
