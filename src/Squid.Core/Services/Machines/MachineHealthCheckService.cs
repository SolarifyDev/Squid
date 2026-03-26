using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Script;

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
                                                     """;

    internal const int DefaultIntervalSeconds = 3600;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

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

    private async Task ExecuteConnectivityCheckAsync(Machine machine, IDeploymentTransport transport, MachineConnectivityPolicyDto connectivityPolicy, CancellationToken cancellationToken)
    {
        Log.Information("Running connectivity check for machine {MachineName} ({Style})", machine.Name, transport.CommunicationStyle);

        if (transport.HealthChecker == null)
        {
            await RecordUnavailableAsync(machine, $"Connectivity check not supported for {transport.CommunicationStyle}", cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = await transport.HealthChecker.CheckConnectivityAsync(machine, connectivityPolicy, cancellationToken).ConfigureAwait(false);
        var status = result.Healthy ? MachineHealthStatus.Healthy : MachineHealthStatus.Unavailable;

        await RecordHealthStatusAsync(machine, status, result.Detail, cancellationToken).ConfigureAwait(false);

        Log.Information("Connectivity check for {MachineName}: {Status}", machine.Name, status);
    }

    private async Task RecordHealthStatusAsync(Machine machine, MachineHealthStatus status, string detail, CancellationToken cancellationToken)
    {
        machine.HealthStatus = status;
        machine.HealthLastChecked = DateTimeOffset.UtcNow;
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

        var healthCheckType = healthCheckPolicy?.HealthCheckType ?? PolicyHealthCheckType.RunScript;

        if (healthCheckType == PolicyHealthCheckType.OnlyConnectivity)
        {
            var connectivityPolicy = await LoadConnectivityPolicyAsync(machine, cancellationToken).ConfigureAwait(false);

            await ExecuteConnectivityCheckAsync(machine, transport, connectivityPolicy, cancellationToken).ConfigureAwait(false);
            return;
        }

        var syntax = transport.HealthChecker?.ScriptSyntax ?? ScriptSyntax.Bash;
        var scriptPolicy = ResolveScriptPolicy(healthCheckPolicy, syntax);
        var defaultScriptPolicy = await LoadDefaultScriptPolicyAsync(syntax, cancellationToken).ConfigureAwait(false);
        var scriptBody = ResolveScriptBody(scriptPolicy, defaultScriptPolicy, transport);

        await ExecuteScriptHealthCheckAsync(machine, transport, scriptBody, cancellationToken).ConfigureAwait(false);
    }

    internal static bool ShouldRunHealthCheck(Machine machine, MachineHealthCheckPolicyDto healthCheckPolicy)
    {
        var scheduleType = healthCheckPolicy?.HealthCheckScheduleType ?? HealthCheckScheduleType.Interval;

        if (scheduleType == HealthCheckScheduleType.Never) return false;
        if (machine.HealthLastChecked == null) return true;

        return scheduleType switch
        {
            HealthCheckScheduleType.Cron => IsCronDue(machine.HealthLastChecked.Value, healthCheckPolicy.HealthCheckCronExpression),
            _ => IsHealthCheckDue(machine.HealthLastChecked.Value, healthCheckPolicy?.HealthCheckIntervalSeconds ?? DefaultIntervalSeconds),
        };
    }

    internal static bool IsHealthCheckDue(DateTimeOffset lastCheckedUtc, int intervalSeconds)
    {
        var nextCheckDue = lastCheckedUtc.AddSeconds(intervalSeconds);

        return DateTimeOffset.UtcNow >= nextCheckDue;
    }

    internal static bool IsCronDue(DateTimeOffset lastChecked, string cronExpression)
    {
        if (string.IsNullOrWhiteSpace(cronExpression)) return false;

        try
        {
            var cron = CronExpression.Parse(cronExpression);
            var nextOccurrence = cron.GetNextOccurrence(lastChecked.UtcDateTime, inclusive: false);

            return nextOccurrence.HasValue && DateTime.UtcNow >= nextOccurrence.Value;
        }
        catch (CronFormatException)
        {
            Log.Warning("Invalid cron expression {CronExpression}, skipping schedule", cronExpression);
            return false;
        }
    }

    internal static MachineScriptPolicyDto ResolveScriptPolicy(MachineHealthCheckPolicyDto healthCheckPolicy, ScriptSyntax syntax)
    {
        if (healthCheckPolicy == null) return null;

        return healthCheckPolicy.ScriptPolicies.GetValueOrDefault(syntax.ToString());
    }

    internal static string ResolveScriptBody(MachineScriptPolicyDto scriptPolicy, MachineScriptPolicyDto defaultScriptPolicy, IDeploymentTransport transport)
    {
        if (scriptPolicy is { RunType: not ScriptPolicyRunType.InheritFromDefault, ScriptBody.Length: > 0 })
            return scriptPolicy.ScriptBody;

        if (defaultScriptPolicy is { RunType: ScriptPolicyRunType.CustomScript, ScriptBody.Length: > 0 })
            return defaultScriptPolicy.ScriptBody;

        return transport.HealthChecker?.DefaultHealthCheckScript ?? FallbackHealthCheckScript;
    }

    private async Task<MachineHealthCheckPolicyDto> LoadHealthCheckPolicyAsync(Machine machine, CancellationToken cancellationToken)
    {
        if (machine.MachinePolicyId == null) return null;

        var policy = await _policyDataProvider.GetByIdAsync(machine.MachinePolicyId.Value, cancellationToken).ConfigureAwait(false);

        if (policy == null) return null;

        return DeserializePolicy<MachineHealthCheckPolicyDto>(policy.MachineHealthCheckPolicy);
    }

    private async Task<MachineScriptPolicyDto> LoadDefaultScriptPolicyAsync(ScriptSyntax syntax, CancellationToken cancellationToken)
    {
        var defaultPolicy = await _policyDataProvider.GetDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (defaultPolicy == null) return null;

        var healthCheckPolicy = DeserializePolicy<MachineHealthCheckPolicyDto>(defaultPolicy.MachineHealthCheckPolicy);

        return ResolveScriptPolicy(healthCheckPolicy, syntax);
    }

    private async Task<MachineConnectivityPolicyDto> LoadConnectivityPolicyAsync(Machine machine, CancellationToken cancellationToken)
    {
        if (machine.MachinePolicyId == null) return null;

        var policy = await _policyDataProvider.GetByIdAsync(machine.MachinePolicyId.Value, cancellationToken).ConfigureAwait(false);

        if (policy == null) return null;

        return DeserializePolicy<MachineConnectivityPolicyDto>(policy.MachineConnectivityPolicy);
    }

    private static T DeserializePolicy<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
