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
    private readonly ISshExecutionMutex _executionMutex;

    public SshExecutionStrategy(ISshConnectionFactory connectionFactory, ISshExecutionMutex executionMutex)
    {
        _connectionFactory = connectionFactory;
        _executionMutex = executionMutex;
    }

    public async Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
    {
        SshConnectionInfo connectionInfo = null;

        try
        {
            connectionInfo = BuildConnectionInfo(request);

            using var executionLock = await _executionMutex.AcquireAsync(connectionInfo.Host, connectionInfo.Port, SshExecutionMutex.DefaultTimeout, ct).ConfigureAwait(false);
            using var scope = _connectionFactory.CreateScope(connectionInfo);

            var remoteWorkDir = ResolveVariable(request.Variables, SpecialVariables.Ssh.RemoteWorkingDirectory);
            var resolvedBase = SshPaths.ResolveBaseDirectory(scope.GetSshClient(), remoteWorkDir);
            var workDir = SshPaths.WorkDirectory(request.ServerTaskId, resolvedBase);

            PrepareRemoteWorkDirectory(scope, workDir, request);

            var result = await ExecuteScriptAsync(scope, workDir, resolvedBase, request, ct).ConfigureAwait(false);

            CleanupRemoteWorkDirectory(scope, workDir, resolvedBase);

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

    private async Task<ScriptExecutionResult> ExecuteScriptAsync(ISshConnectionScope scope, string workDir, string baseDir, ScriptExecutionRequest request, CancellationToken ct)
    {
        var sftp = scope.GetSftpClient();
        var ssh = scope.GetSshClient();

        var scriptName = ResolveScriptFileName(request);
        var scriptPath = SshPaths.ScriptPath(workDir, scriptName);
        var scriptContent = BootstrapIfBash(request, workDir, baseDir);
        var scriptBytes = Encoding.UTF8.GetBytes(scriptContent);

        SshFileTransfer.UploadBytesVerified(sftp, ssh, scriptBytes, scriptPath);

        var chmodResult = SshRemoteShellExecutor.Execute(ssh, $"chmod +x \"{scriptPath}\"", TimeSpan.FromSeconds(10));

        if (chmodResult.ExitCode != 0)
            Log.Warning("[SSH] chmod +x failed for {Path}: {Error}", scriptPath, chmodResult.Error);

        var command = BuildExecutionCommand(scriptName, workDir, request);
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

    private static void CleanupRemoteWorkDirectory(ISshConnectionScope scope, string workDir, string baseDir)
    {
        try
        {
            var ssh = scope.GetSshClient();
            SshRemoteShellExecutor.Execute(ssh, $"rm -rf \"{workDir}\"", TimeSpan.FromSeconds(15));

            CleanupOldWorkDirectories(scope, baseDir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SSH] Failed to clean up remote work directory {WorkDir}", workDir);
        }
    }

    internal const int RetentionKeepCount = 10;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);

    private static void CleanupOldWorkDirectories(ISshConnectionScope scope, string baseDir)
    {
        try
        {
            var ssh = scope.GetSshClient();
            var workParent = $"{baseDir}/Work";

            // List directories sorted oldest-first, skip the most recent N, delete the rest
            var cleanupCommand = $"cd \"{workParent}\" 2>/dev/null && ls -1dt */ 2>/dev/null | tail -n +{RetentionKeepCount + 1} | xargs -I {{}} rm -rf \"{{}}\"";
            SshRemoteShellExecutor.Execute(ssh, cleanupCommand, CleanupTimeout);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[SSH] Best-effort cleanup of old work directories failed");
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

        var proxyTypeStr = ResolveVariable(request.Variables, SpecialVariables.Ssh.ProxyType);
        Enum.TryParse<Message.Enums.SshProxyType>(proxyTypeStr, ignoreCase: true, out var proxyType);
        var proxyHost = ResolveVariable(request.Variables, SpecialVariables.Ssh.ProxyHost);
        var proxyPort = int.TryParse(ResolveVariable(request.Variables, SpecialVariables.Ssh.ProxyPort), out var pp) ? pp : 0;
        var proxyUsername = ResolveVariable(request.Variables, SpecialVariables.Ssh.ProxyUsername);
        var proxyPassword = ResolveVariable(request.Variables, SpecialVariables.Ssh.ProxyPassword);

        return new SshConnectionInfo(host, port, username, privateKey, passphrase, password, fingerprint, TimeSpan.FromSeconds(30), proxyType, proxyHost, proxyPort, proxyUsername, proxyPassword);
    }

    private static string ResolveVariable(List<VariableDto> variables, string name)
    {
        return variables?
            .FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Value ?? string.Empty;
    }

    internal static string BootstrapIfBash(ScriptExecutionRequest request, string workDir, string baseDir)
    {
        if (request.Syntax is Message.Models.Deployments.Execution.ScriptSyntax.PowerShell or Message.Models.Deployments.Execution.ScriptSyntax.Python)
            return request.ScriptBody ?? string.Empty;

        return SshBootstrapper.WrapBashScript(request.ScriptBody, workDir, request.ServerTaskId, baseDir);
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

    private static string BuildExecutionCommand(string scriptName, string workDir, ScriptExecutionRequest request)
    {
        var runner = request.Syntax switch
        {
            Message.Models.Deployments.Execution.ScriptSyntax.PowerShell => $"pwsh -NoProfile -NonInteractive -File \"./{scriptName}\"",
            Message.Models.Deployments.Execution.ScriptSyntax.Python => $"python3 \"./{scriptName}\"",
            _ => $"bash \"./{scriptName}\""
        };

        return $"cd \"{workDir}\" && {runner}";
    }

    private static List<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();

        return text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
    }
}
