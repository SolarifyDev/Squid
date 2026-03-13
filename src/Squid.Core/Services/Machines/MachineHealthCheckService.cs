using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.Machines;

public interface IMachineHealthCheckService : IScopedDependency
{
    Task ManualHealthCheckAsync(int machineId, CancellationToken cancellationToken = default);

    Task AutoHealthCheckForAllAsync(CancellationToken cancellationToken = default);
}

public class MachineHealthCheckService : IMachineHealthCheckService
{
    private const string FallbackHealthCheckScript = """
                                                     #!/bin/bash
                                                     echo "Health check started"
                                                     echo "Hostname: $(hostname)"
                                                     echo "Date: $(date -u)"
                                                     echo "Uptime: $(uptime)"
                                                     echo "Health check completed"
                                                     exit 0
                                                     """;

    internal const int DefaultIntervalSeconds = 3600;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IMachineDataProvider _machineDataProvider;
    private readonly IMachinePolicyDataProvider _policyDataProvider;
    private readonly ITransportRegistry _transportRegistry;

    public MachineHealthCheckService(IMachineDataProvider machineDataProvider, IMachinePolicyDataProvider policyDataProvider, ITransportRegistry transportRegistry)
    {
        _machineDataProvider = machineDataProvider;
        _policyDataProvider = policyDataProvider;
        _transportRegistry = transportRegistry;
    }

    public async Task ManualHealthCheckAsync(int machineId, CancellationToken cancellationToken = default)
    {
        var machine = await LoadMachineAsync(machineId, cancellationToken).ConfigureAwait(false);

        if (machine.IsDisabled)
        {
            Log.Information("Skipping health check for disabled machine {MachineName}", machine.Name);
            return;
        }

        var transport = ResolveTransport(machine);

        if (transport == null)
        {
            await RecordUnavailableAsync(machine, $"No transport for {CommunicationStyleParser.Parse(machine.Endpoint)}", cancellationToken).ConfigureAwait(false);
            return;
        }

        var scriptBody = transport.HealthChecker?.DefaultHealthCheckScript ?? FallbackHealthCheckScript;

        await ExecuteScriptHealthCheckAsync(machine, transport, scriptBody, cancellationToken).ConfigureAwait(false);
    }

    public async Task AutoHealthCheckForAllAsync(CancellationToken cancellationToken = default)
    {
        var (_, machines) = await _machineDataProvider.GetMachinePagingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var activeMachines = machines.Where(m => !m.IsDisabled).ToList();

        Log.Information("Running auto health check sweep for {Count} active machines", activeMachines.Count);

        var checkedCount = 0;

        foreach (var machine in activeMachines)
        {
            try
            {
                var healthCheckPolicy = await LoadHealthCheckPolicyAsync(machine, cancellationToken).ConfigureAwait(false);

                if (!ShouldRunHealthCheck(machine, healthCheckPolicy))
                    continue;

                await ExecutePolicyHealthCheckAsync(machine, healthCheckPolicy, cancellationToken).ConfigureAwait(false);
                checkedCount++;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Health check failed for machine {MachineName}", machine.Name);
                await RecordUnavailableAsync(machine, $"Health check error: {ex.Message}", cancellationToken).ConfigureAwait(false);
                checkedCount++;
            }
        }

        Log.Information("Auto health check sweep completed. Checked {CheckedCount} of {TotalCount} machines", checkedCount, activeMachines.Count);
    }

    // ========================================================================
    // Shared pipeline methods
    // ========================================================================

    private async Task<Machine> LoadMachineAsync(int machineId, CancellationToken cancellationToken)
    {
        var machine = await _machineDataProvider.GetMachinesByIdAsync(machineId, cancellationToken).ConfigureAwait(false);

        if (machine == null)
            throw new InvalidOperationException($"Machine {machineId} not found");

        return machine;
    }

    private IDeploymentTransport ResolveTransport(Machine machine)
    {
        var style = CommunicationStyleParser.Parse(machine.Endpoint);
        var transport = _transportRegistry.Resolve(style);

        if (transport == null)
            Log.Warning("No transport found for machine {MachineName} with style {Style}", machine.Name, style);

        return transport;
    }

    private async Task ExecuteScriptHealthCheckAsync(Machine machine, IDeploymentTransport transport, string scriptBody, CancellationToken cancellationToken)
    {
        var request = new ScriptExecutionRequest
        {
            Machine = machine,
            ScriptBody = scriptBody,
            ExecutionMode = ExecutionMode.DirectScript,
            Syntax = ScriptSyntax.Bash,
            Files = new Dictionary<string, byte[]>(),
            Variables = new List<Message.Models.Deployments.Variable.VariableDto>()
        };

        Log.Information("Running health check for machine {MachineName} ({Style})", machine.Name, transport.CommunicationStyle);

        var result = await transport.Strategy.ExecuteScriptAsync(request, cancellationToken).ConfigureAwait(false);

        var status = result.Success ? MachineHealthStatus.Healthy : MachineHealthStatus.Unhealthy;
        var detail = string.Join("\n", result.LogLines ?? new List<string>());

        await RecordHealthStatusAsync(machine, status, detail, cancellationToken).ConfigureAwait(false);

        Log.Information("Health check for {MachineName}: {Status}", machine.Name, status);
    }

    private async Task ExecuteConnectivityCheckAsync(Machine machine, IDeploymentTransport transport, CancellationToken cancellationToken)
    {
        Log.Information("Running connectivity check for machine {MachineName} ({Style})", machine.Name, transport.CommunicationStyle);

        if (transport.HealthChecker == null)
        {
            await RecordUnavailableAsync(machine, $"Connectivity check not supported for {transport.CommunicationStyle}", cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = await transport.HealthChecker.CheckConnectivityAsync(machine, cancellationToken).ConfigureAwait(false);
        var status = result.Healthy ? MachineHealthStatus.Healthy : MachineHealthStatus.Unavailable;

        await RecordHealthStatusAsync(machine, status, result.Detail, cancellationToken).ConfigureAwait(false);

        Log.Information("Connectivity check for {MachineName}: {Status}", machine.Name, status);
    }

    private async Task RecordHealthStatusAsync(Machine machine, MachineHealthStatus status, string detail, CancellationToken cancellationToken)
    {
        machine.HealthStatus = status;
        machine.HealthLastChecked = DateTime.UtcNow;
        machine.HealthDetail = detail;

        await _machineDataProvider.UpdateMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private Task RecordUnavailableAsync(Machine machine, string detail, CancellationToken cancellationToken)
        => RecordHealthStatusAsync(machine, MachineHealthStatus.Unavailable, detail, cancellationToken);

    // ========================================================================
    // Auto (policy-driven) helpers
    // ========================================================================

    private async Task ExecutePolicyHealthCheckAsync(Machine machine, MachineHealthCheckPolicyDto healthCheckPolicy, CancellationToken cancellationToken)
    {
        var transport = ResolveTransport(machine);

        if (transport == null)
        {
            await RecordUnavailableAsync(machine, $"No transport for {CommunicationStyleParser.Parse(machine.Endpoint)}", cancellationToken).ConfigureAwait(false);
            return;
        }

        var style = transport.CommunicationStyle;
        var scriptPolicy = ResolveScriptPolicy(healthCheckPolicy, style);

        if (string.Equals(scriptPolicy?.RunType, "OnlyConnectivity", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteConnectivityCheckAsync(machine, transport, cancellationToken).ConfigureAwait(false);
            return;
        }

        var scriptBody = ResolveScriptBody(scriptPolicy, transport);

        await ExecuteScriptHealthCheckAsync(machine, transport, scriptBody, cancellationToken).ConfigureAwait(false);
    }

    internal static bool ShouldRunHealthCheck(Machine machine, MachineHealthCheckPolicyDto healthCheckPolicy)
    {
        if (machine.HealthLastChecked == null) return true;

        var intervalSeconds = healthCheckPolicy?.HealthCheckIntervalSeconds ?? DefaultIntervalSeconds;

        return IsHealthCheckDue(machine.HealthLastChecked.Value, intervalSeconds);
    }

    internal static bool IsHealthCheckDue(DateTime lastCheckedUtc, int intervalSeconds)
    {
        var nextCheckDue = lastCheckedUtc.AddSeconds(intervalSeconds);

        return DateTime.UtcNow >= nextCheckDue;
    }

    internal static MachineScriptPolicyDto ResolveScriptPolicy(MachineHealthCheckPolicyDto healthCheckPolicy, CommunicationStyle style)
    {
        if (healthCheckPolicy == null) return null;

        var styleKey = style.ToString();

        return healthCheckPolicy.ScriptPolicies.GetValueOrDefault(styleKey);
    }

    private static string ResolveScriptBody(MachineScriptPolicyDto scriptPolicy, IDeploymentTransport transport)
    {
        if (scriptPolicy != null && scriptPolicy.RunType != "InheritFromDefault" && !string.IsNullOrWhiteSpace(scriptPolicy.ScriptBody))
            return scriptPolicy.ScriptBody;

        return transport.HealthChecker?.DefaultHealthCheckScript ?? FallbackHealthCheckScript;
    }

    private async Task<MachineHealthCheckPolicyDto> LoadHealthCheckPolicyAsync(Machine machine, CancellationToken cancellationToken)
    {
        if (machine.MachinePolicyId == null) return null;

        var policy = await _policyDataProvider.GetByIdAsync(machine.MachinePolicyId.Value, cancellationToken).ConfigureAwait(false);

        if (policy == null) return null;

        return DeserializePolicy(policy.MachineHealthCheckPolicy);
    }

    private static MachineHealthCheckPolicyDto DeserializePolicy(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<MachineHealthCheckPolicyDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
