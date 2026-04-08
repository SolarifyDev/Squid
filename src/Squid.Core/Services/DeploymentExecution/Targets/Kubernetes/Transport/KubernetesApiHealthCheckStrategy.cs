using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public class KubernetesApiHealthCheckStrategy : IHealthCheckStrategy
{
    private const string HealthCheckScript = """
                                             #!/bin/bash
                                             set -e
                                             echo "Health check started (KubernetesApi)"
                                             echo "Hostname: $(hostname)"
                                             echo "Date: $(date -u)"
                                             kubectl version 2>&1
                                             echo "Health check completed"
                                             """;

    private readonly ITargetScriptRunner _scriptRunner;

    public KubernetesApiHealthCheckStrategy(ITargetScriptRunner scriptRunner)
    {
        _scriptRunner = scriptRunner;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(Machine machine, MachineConnectivityPolicyDto connectivityPolicy, CancellationToken ct, MachineHealthCheckPolicyDto healthCheckPolicy = null)
    {
        var endpoint = EndpointVariableFactory.TryDeserialize<KubernetesApiEndpointDto>(machine.Endpoint);

        if (endpoint == null)
            return new HealthCheckResult(false, "Failed to parse endpoint JSON");

        if (string.IsNullOrEmpty(endpoint.ClusterUrl))
            return new HealthCheckResult(false, "ClusterUrl is empty");

        try
        {
            var result = await _scriptRunner.RunAsync(machine, HealthCheckScript, ScriptSyntax.Bash, ct).ConfigureAwait(false);

            return result.Success
                ? new HealthCheckResult(true, $"Health check passed (exit code {result.ExitCode}): {string.Join("\n", result.LogLines ?? new())}")
                : new HealthCheckResult(false, $"Health check failed (exit code {result.ExitCode}): {result.BuildErrorSummary()}");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(false, $"Health check error: {ex.Message}");
        }
    }
}
