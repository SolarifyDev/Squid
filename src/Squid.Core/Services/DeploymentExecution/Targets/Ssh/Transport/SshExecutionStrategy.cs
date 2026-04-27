using System.Text;
using Renci.SshNet;
using Serilog;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Packages.Staging;
using Squid.Core.Services.DeploymentExecution.Runtime;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Core.Services.DeploymentExecution.Ssh.Packages;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public class SshExecutionStrategy : IExecutionStrategy
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly ISshConnectionFactory _connectionFactory;
    private readonly ISshExecutionMutex _executionMutex;
    private readonly IPackageStagingPlanner _stagingPlanner;
    private readonly IRuntimeBundleProvider _runtimeBundleProvider;

    public SshExecutionStrategy(ISshConnectionFactory connectionFactory, ISshExecutionMutex executionMutex, IPackageStagingPlanner stagingPlanner, IRuntimeBundleProvider runtimeBundleProvider)
    {
        _connectionFactory = connectionFactory;
        _executionMutex = executionMutex;
        _stagingPlanner = stagingPlanner;
        _runtimeBundleProvider = runtimeBundleProvider;
    }

    public async Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
    {
        SshConnectionInfo connectionInfo = null;

        try
        {
            connectionInfo = BuildConnectionInfo(request);

            using var executionLock = await _executionMutex.AcquireAsync(connectionInfo.Host, connectionInfo.Port, SshExecutionMutex.DefaultTimeout, ct).ConfigureAwait(false);
            using var scope = _connectionFactory.CreateScope(connectionInfo);

            return await ExecuteWithScopeAsync(scope, request, ct).ConfigureAwait(false);
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

    /// <summary>
    /// P0-Phase9.2 — cleanup-finally guarantee: workDir cleanup runs on EVERY
    /// exit path (success, exception, cancellation), not just success.
    ///
    /// <para><b>The bug pre-Phase-9.2</b>: <c>CleanupRemoteWorkDirectory</c> sat
    /// on the success path. If <c>PrepareRemoteWorkDirectoryAsync</c> threw
    /// after creating the work dir on remote, OR if <c>ExecuteScriptAsync</c>
    /// threw after dropping <c>sensitiveVariables.json</c> to disk, the work
    /// dir was orphaned forever. Result: <c>/tmp</c> filled with stale dirs
    /// AND decrypted password files remained world-readable on the remote.</para>
    ///
    /// <para>The cleanup helper is itself wrapped in try/catch (see
    /// <see cref="CleanupRemoteWorkDirectory"/>) — it cannot itself bubble an
    /// exception out of the finally block, so wrapping in finally is safe.</para>
    /// </summary>
    private async Task<ScriptExecutionResult> ExecuteWithScopeAsync(ISshConnectionScope scope, ScriptExecutionRequest request, CancellationToken ct)
    {
        var remoteWorkDir = ResolveVariable(request.Variables, SpecialVariables.Ssh.RemoteWorkingDirectory);
        var resolvedBase = SshPaths.ResolveBaseDirectory(scope.GetSshClient(), remoteWorkDir);
        var workDir = SshPaths.WorkDirectory(request.ServerTaskId, resolvedBase);

        try
        {
            await PrepareRemoteWorkDirectoryAsync(scope, workDir, resolvedBase, request, ct).ConfigureAwait(false);

            return await ExecuteScriptAsync(scope, workDir, resolvedBase, request, ct).ConfigureAwait(false);
        }
        finally
        {
            // Best-effort cleanup; CleanupRemoteWorkDirectory swallows its own
            // exceptions to log + continue. Runs on success, exception, AND
            // cancellation — the entire reason this finally block exists.
            CleanupRemoteWorkDirectory(scope, workDir, resolvedBase);
        }
    }

    private async Task PrepareRemoteWorkDirectoryAsync(ISshConnectionScope scope, string workDir, string baseDir, ScriptExecutionRequest request, CancellationToken ct)
    {
        var sftp = scope.GetSftpClient();

        SshFileTransfer.EnsureDirectoryExists(sftp, workDir);

        UploadScriptFiles(sftp, workDir, request.DeploymentFiles);

        await StageAndExtractPackagesAsync(scope, baseDir, request.PackageReferences, ct).ConfigureAwait(false);
    }

    private static void UploadScriptFiles(SftpClient sftp, string workDir, DeploymentFileCollection files)
    {
        if (files == null || files.Count == 0) return;

        foreach (var file in files)
        {
            ValidateRelativePath(file.RelativePath);
            var remotePath = SshPaths.ScriptPath(workDir, file.RelativePath);
            SshFileTransfer.UploadBytes(sftp, file.Content, remotePath);
        }
    }

    private async Task StageAndExtractPackagesAsync(ISshConnectionScope scope, string baseDir, List<PackageAcquisitionResult> packages, CancellationToken ct)
    {
        if (packages == null || packages.Count == 0) return;

        var stagingContext = new SshPackageStagingContext(scope, baseDir);
        var sftp = scope.GetSftpClient();
        var ssh = scope.GetSshClient();

        foreach (var pkg in packages)
        {
            var requirement = new PackageRequirement(pkg.PackageId, pkg.Version, pkg.LocalPath, pkg.SizeBytes, pkg.Hash);

            var plan = await _stagingPlanner.PlanAsync(requirement, stagingContext, ct).ConfigureAwait(false);

            var extractDir = SshPaths.PackageExtractDir(baseDir, pkg.PackageId, pkg.Version);
            SshPackageTransfer.ExtractPackage(sftp, ssh, plan.RemotePath, extractDir);
        }
    }

    /// <summary>
    /// Validates a relative upload path. Forward-slash nesting (e.g. <c>content/values.yaml</c>)
    /// is accepted; rooted paths, backslashes, and <c>..</c> traversal segments are rejected.
    /// Mirrors <see cref="DeploymentFile.EnsureValid"/> but is retained here as a defensive
    /// check at the transport seam until handlers emit typed <see cref="DeploymentFile"/>
    /// entries end-to-end.
    /// </summary>
    internal static void ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Relative path cannot be empty");

        if (relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
            throw new ArgumentException($"Relative path must not be rooted: {relativePath}");

        if (relativePath.Length >= 2 && relativePath[1] == ':')
            throw new ArgumentException($"Relative path must not contain a drive letter: {relativePath}");

        if (relativePath.Contains('\\'))
            throw new ArgumentException($"Relative path must use forward slashes: {relativePath}");

        foreach (var segment in relativePath.Split('/'))
        {
            if (segment == "..")
                throw new ArgumentException($"Relative path must not contain '..' segments: {relativePath}");
        }
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

    /// <summary>
    /// <c>protected internal virtual</c> so unit tests can observe / count calls
    /// via a test subclass override. Production behaviour: best-effort
    /// <c>rm -rf</c> of the workDir + retention sweep over older sibling dirs.
    /// Swallows all exceptions to keep finally-block invocations safe.
    /// </summary>
    protected internal virtual void CleanupRemoteWorkDirectory(ISshConnectionScope scope, string workDir, string baseDir)
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

    internal string BootstrapIfBash(ScriptExecutionRequest request, string workDir, string baseDir)
    {
        if (request.Syntax is Message.Models.Deployments.Execution.ScriptSyntax.PowerShell or Message.Models.Deployments.Execution.ScriptSyntax.Python)
            return request.ScriptBody ?? string.Empty;

        var bundle = _runtimeBundleProvider.GetBundle(RuntimeBundleKind.Bash);

        return bundle.Wrap(new RuntimeBundleWrapContext
        {
            UserScriptBody = request.ScriptBody,
            WorkDirectory = workDir,
            BaseDirectory = baseDir,
            ServerTaskId = request.ServerTaskId,
            Variables = request.Variables ?? (IReadOnlyList<VariableDto>)Array.Empty<VariableDto>()
        });
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
