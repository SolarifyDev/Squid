using Serilog;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.DeploymentExecution.Ssh;

public class SshHealthCheckStrategy : IHealthCheckStrategy
{
    internal const int DefaultConnectTimeoutSeconds = 15;
    internal static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(5);

    private readonly IEndpointContextBuilder _endpointContextBuilder;
    private readonly ISshConnectionFactory _connectionFactory;

    public SshHealthCheckStrategy(IEndpointContextBuilder endpointContextBuilder, ISshConnectionFactory connectionFactory)
    {
        _endpointContextBuilder = endpointContextBuilder;
        _connectionFactory = connectionFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(Machine machine, MachineConnectivityPolicyDto connectivityPolicy, CancellationToken ct, MachineHealthCheckPolicyDto healthCheckPolicy = null)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<SshEndpointDto>(machine.Endpoint);

        if (endpoint == null)
            return new HealthCheckResult(false, "Failed to parse SSH endpoint JSON");

        if (string.IsNullOrEmpty(endpoint.Host))
            return new HealthCheckResult(false, "SSH host is empty");

        try
        {
            var connectionInfo = await BuildConnectionInfoAsync(machine, endpoint, connectivityPolicy, ct).ConfigureAwait(false);
            var customScript = ResolveCustomScript(healthCheckPolicy);

            return await Task.Run(() => ProbeConnectivity(connectionInfo, customScript), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SSH] Health check failed for {MachineName}", machine.Name);
            return new HealthCheckResult(false, $"SSH health check error: {ex.Message}");
        }
    }

    private async Task<SshConnectionInfo> BuildConnectionInfoAsync(Machine machine, SshEndpointDto endpoint, MachineConnectivityPolicyDto connectivityPolicy, CancellationToken ct)
    {
        var contributor = new SshEndpointVariableContributor();
        var endpointContext = await _endpointContextBuilder.BuildAsync(machine.Endpoint, contributor, ct).ConfigureAwait(false);

        var accountData = endpointContext.GetAccountData();
        var accountVars = AccountVariableExpander.Expand(accountData);

        var username = FindVariable(accountVars, SpecialVariables.Account.Username);
        var privateKey = FindVariable(accountVars, SpecialVariables.Account.SshPrivateKeyFile);
        var passphrase = FindVariable(accountVars, SpecialVariables.Account.SshPassphrase);
        var password = FindVariable(accountVars, SpecialVariables.Account.Password);

        var timeout = TimeSpan.FromSeconds(connectivityPolicy?.ConnectTimeoutSeconds ?? DefaultConnectTimeoutSeconds);

        return new SshConnectionInfo(endpoint.Host, endpoint.Port, username, privateKey, passphrase, password, endpoint.Fingerprint, timeout, endpoint.ProxyType, endpoint.ProxyHost, endpoint.ProxyPort, endpoint.ProxyUsername, endpoint.ProxyPassword);
    }

    private HealthCheckResult ProbeConnectivity(SshConnectionInfo connectionInfo, string customScript)
    {
        using var scope = _connectionFactory.CreateScope(connectionInfo);

        // Test 1: SSH connectivity
        var ssh = scope.GetSshClient();
        var echoResult = SshRemoteShellExecutor.Execute(ssh, "echo ok", TimeSpan.FromSeconds(10));

        if (echoResult.ExitCode != 0)
            return new HealthCheckResult(false, $"SSH command failed (exit code {echoResult.ExitCode}): {echoResult.Error}");

        // Test 2: SFTP connectivity (with retry for transient errors)
        var sftp = scope.GetSftpClient();
        SshRetryHelper.ExecuteWithRetry(() => { sftp.ListDirectory("."); }, SshTransientErrorDetector.IsTransient);

        // Test 3: Custom health check script (if configured)
        if (!string.IsNullOrWhiteSpace(customScript))
        {
            var scriptResult = SshRemoteShellExecutor.Execute(ssh, customScript, ScriptTimeout);

            if (scriptResult.ExitCode != 0)
                return new HealthCheckResult(false, $"Custom health check script failed (exit code {scriptResult.ExitCode}): {scriptResult.Error}");

            Log.Information("[SSH] Custom health check script passed on {Host}", connectionInfo.Host);
        }

        return new HealthCheckResult(true, $"SSH health check passed — {connectionInfo.Host}:{connectionInfo.Port}");
    }

    internal static string ResolveCustomScript(MachineHealthCheckPolicyDto healthCheckPolicy)
    {
        if (healthCheckPolicy?.HealthCheckType != PolicyHealthCheckType.RunScript) return null;
        if (healthCheckPolicy.ScriptPolicies == null) return null;

        // SSH targets use Bash scripts
        if (healthCheckPolicy.ScriptPolicies.TryGetValue("Bash", out var bashPolicy) && bashPolicy?.RunType == ScriptPolicyRunType.CustomScript)
            return bashPolicy.ScriptBody;

        return null;
    }

    private static string FindVariable(List<Message.Models.Deployments.Variable.VariableDto> vars, string name)
    {
        return vars?.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
    }
}
