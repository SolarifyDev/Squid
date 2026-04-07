using System.Text;
using Serilog;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public class SshExecutionStrategy : IExecutionStrategy
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly ISshConnectionFactory _connectionFactory;

    public SshExecutionStrategy(ISshConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
    {
        SshConnectionInfo connectionInfo = null;

        try
        {
            connectionInfo = BuildConnectionInfo(request);

            using var scope = _connectionFactory.CreateScope(connectionInfo);

            var remoteWorkDir = ResolveVariable(request.Variables, SpecialVariables.Ssh.RemoteWorkingDirectory);
            var workDir = SshPaths.WorkDirectory(request.ServerTaskId, remoteWorkDir);

            PrepareRemoteWorkDirectory(scope, workDir, request);

            var result = await ExecuteScriptAsync(scope, workDir, request, ct).ConfigureAwait(false);

            CleanupRemoteWorkDirectory(scope, workDir);

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Log.Warning("[SSH] Execution timed out on {Host}", connectionInfo?.Host ?? "unknown");

            return new ScriptExecutionResult
            {
                Success = false,
                ExitCode = ScriptExitCodes.Timeout,
                LogLines = new List<string> { $"SSH script execution timed out" },
                StderrLines = new List<string> { "Timeout" }
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SSH] Execution failed on {Host}", connectionInfo?.Host ?? "unknown");

            return new ScriptExecutionResult
            {
                Success = false,
                ExitCode = -1,
                LogLines = new List<string> { $"SSH execution error: {ex.Message}" },
                StderrLines = new List<string> { ex.Message }
            };
        }
    }

    private void PrepareRemoteWorkDirectory(ISshConnectionScope scope, string workDir, ScriptExecutionRequest request)
    {
        var sftp = scope.GetSftpClient();

        SshFileTransfer.EnsureDirectoryExists(sftp, workDir);

        if (request.Files == null) return;

        foreach (var file in request.Files)
        {
            ValidateFileName(file.Key);

            var remotePath = SshPaths.ScriptPath(workDir, file.Key);
            SshFileTransfer.UploadBytes(sftp, file.Value, remotePath);
        }
    }

    internal static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            throw new ArgumentException("File name cannot be empty");

        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            throw new ArgumentException($"File name contains invalid path characters: {fileName}");
    }

    private async Task<ScriptExecutionResult> ExecuteScriptAsync(ISshConnectionScope scope, string workDir, ScriptExecutionRequest request, CancellationToken ct)
    {
        var sftp = scope.GetSftpClient();
        var ssh = scope.GetSshClient();

        var scriptName = ResolveScriptFileName(request);
        var scriptPath = SshPaths.ScriptPath(workDir, scriptName);
        var scriptBytes = Encoding.UTF8.GetBytes(request.ScriptBody ?? string.Empty);

        SshFileTransfer.UploadBytesVerified(sftp, ssh, scriptBytes, scriptPath);

        var chmodResult = SshRemoteShellExecutor.Execute(ssh, $"chmod +x \"{scriptPath}\"", TimeSpan.FromSeconds(10));

        if (chmodResult.ExitCode != 0)
            Log.Warning("[SSH] chmod +x failed for {Path}: {Error}", scriptPath, chmodResult.Error);

        var command = BuildExecutionCommand(scriptPath, workDir, request);
        var scriptTimeout = request.Timeout ?? DefaultTimeout;

        using var timeoutCts = new CancellationTokenSource(scriptTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var result = await SshRemoteShellExecutor.ExecuteAsync(ssh, command, scriptTimeout, linkedCts.Token).ConfigureAwait(false);

        return new ScriptExecutionResult
        {
            Success = result.ExitCode == 0,
            ExitCode = result.ExitCode,
            LogLines = SplitLines(result.Output),
            StderrLines = SplitLines(result.Error)
        };
    }

    private static void CleanupRemoteWorkDirectory(ISshConnectionScope scope, string workDir)
    {
        try
        {
            var ssh = scope.GetSshClient();
            SshRemoteShellExecutor.Execute(ssh, $"rm -rf \"{workDir}\"", TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SSH] Failed to clean up remote work directory {WorkDir}", workDir);
        }
    }

    private static SshConnectionInfo BuildConnectionInfo(ScriptExecutionRequest request)
    {
        var host = ResolveVariable(request.Variables, SpecialVariables.Ssh.Host);
        var port = int.TryParse(ResolveVariable(request.Variables, SpecialVariables.Ssh.Port), out var p) ? p : 22;
        var fingerprint = ResolveVariable(request.Variables, SpecialVariables.Ssh.Fingerprint);

        var username = ResolveVariable(request.Variables, SpecialVariables.Account.Username);
        var privateKey = ResolveVariable(request.Variables, SpecialVariables.Account.SshPrivateKeyFile);
        var passphrase = ResolveVariable(request.Variables, SpecialVariables.Account.SshPassphrase);
        var password = ResolveVariable(request.Variables, SpecialVariables.Account.Password);

        return new SshConnectionInfo(host, port, username, privateKey, passphrase, password, fingerprint, TimeSpan.FromSeconds(30));
    }

    private static string ResolveVariable(List<VariableDto> variables, string name)
    {
        return variables?
            .FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Value ?? string.Empty;
    }

    private static string ResolveScriptFileName(ScriptExecutionRequest request)
    {
        return request.Syntax switch
        {
            Message.Models.Deployments.Execution.ScriptSyntax.PowerShell => "script.ps1",
            Message.Models.Deployments.Execution.ScriptSyntax.Python => "script.py",
            _ => "script.sh"
        };
    }

    private static string BuildExecutionCommand(string scriptPath, string workDir, ScriptExecutionRequest request)
    {
        var runner = request.Syntax switch
        {
            Message.Models.Deployments.Execution.ScriptSyntax.PowerShell => $"pwsh -NoProfile -NonInteractive -File \"{scriptPath}\"",
            Message.Models.Deployments.Execution.ScriptSyntax.Python => $"python3 \"{scriptPath}\"",
            _ => $"bash \"{scriptPath}\""
        };

        return $"cd \"{workDir}\" && {runner}";
    }

    private static List<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();

        return text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
    }
}
