using Renci.SshNet;
using Serilog;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public static class SshRemoteShellExecutor
{
    public static SshCommandResult Execute(SshClient client, string command, TimeSpan timeout)
    {
        Log.Information("[SSH] Executing command on {Host} (timeout={Timeout}s)", client.ConnectionInfo.Host, timeout.TotalSeconds);

        using var sshCommand = client.CreateCommand(command);
        sshCommand.CommandTimeout = timeout;

        var output = sshCommand.Execute();
        var error = sshCommand.Error;
        var exitCode = sshCommand.ExitStatus ?? -1;

        Log.Information("[SSH] Command completed on {Host} with exit code {ExitCode}", client.ConnectionInfo.Host, exitCode);

        return new SshCommandResult(exitCode, output ?? string.Empty, error ?? string.Empty);
    }
}
