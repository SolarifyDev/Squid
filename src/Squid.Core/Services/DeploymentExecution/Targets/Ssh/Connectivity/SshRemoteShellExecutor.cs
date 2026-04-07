using Renci.SshNet;
using Serilog;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public static class SshRemoteShellExecutor
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

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

    public static async Task<SshCommandResult> ExecuteAsync(SshClient client, string command, TimeSpan timeout, CancellationToken ct)
    {
        Log.Information("[SSH] Executing async command on {Host} (timeout={Timeout}s)", client.ConnectionInfo.Host, timeout.TotalSeconds);

        using var sshCommand = client.CreateCommand(command);
        sshCommand.CommandTimeout = timeout;

        var asyncResult = sshCommand.BeginExecute();

        while (!asyncResult.IsCompleted)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        sshCommand.EndExecute(asyncResult);

        var output = sshCommand.Result ?? string.Empty;
        var error = sshCommand.Error ?? string.Empty;
        var exitCode = sshCommand.ExitStatus ?? -1;

        Log.Information("[SSH] Async command completed on {Host} with exit code {ExitCode}", client.ConnectionInfo.Host, exitCode);

        return new SshCommandResult(exitCode, output, error);
    }
}
