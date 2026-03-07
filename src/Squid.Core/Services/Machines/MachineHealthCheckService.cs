using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines;

public interface IMachineHealthCheckService : IScopedDependency
{
    Task RunHealthCheckAsync(int machineId, CancellationToken cancellationToken = default);

    Task RunHealthCheckForAllAsync(CancellationToken cancellationToken = default);
}

public class MachineHealthCheckService : IMachineHealthCheckService
{
    private const string DefaultHealthCheckScript = """
                                                    #!/bin/bash
                                                    echo "Health check started"
                                                    echo "Hostname: $(hostname)"
                                                    echo "Date: $(date -u)"
                                                    echo "Uptime: $(uptime)"
                                                    echo "Health check completed"
                                                    exit 0
                                                    """;

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

    public async Task RunHealthCheckAsync(int machineId, CancellationToken cancellationToken = default)
    {
        var machine = await _machineDataProvider.GetMachinesByIdAsync(machineId, cancellationToken).ConfigureAwait(false);

        if (machine == null)
            throw new InvalidOperationException($"Machine {machineId} not found");

        if (machine.IsDisabled)
        {
            Log.Information("Skipping health check for disabled machine {MachineName}", machine.Name);
            return;
        }

        await ExecuteHealthCheckAsync(machine, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunHealthCheckForAllAsync(CancellationToken cancellationToken = default)
    {
        var (_, machines) = await _machineDataProvider.GetMachinePagingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var activeMachines = machines.Where(m => !m.IsDisabled).ToList();

        Log.Information("Running health checks for {Count} active machines", activeMachines.Count);

        foreach (var machine in activeMachines)
        {
            try
            {
                await ExecuteHealthCheckAsync(machine, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Health check failed for machine {MachineName}", machine.Name);
                await RecordHealthStatusAsync(machine, MachineHealthStatus.Unavailable, $"Health check error: {ex.Message}", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteHealthCheckAsync(Machine machine, CancellationToken cancellationToken)
    {
        var style = CommunicationStyleParser.Parse(machine.Endpoint);
        var transport = _transportRegistry.Resolve(style);

        if (transport == null)
        {
            Log.Warning("No transport found for machine {MachineName} with style {Style}", machine.Name, style);
            await RecordHealthStatusAsync(machine, MachineHealthStatus.Unavailable, $"No transport for {style}", cancellationToken).ConfigureAwait(false);
            return;
        }

        var scriptBody = await ResolveHealthCheckScriptAsync(machine, style, cancellationToken).ConfigureAwait(false);

        var request = new ScriptExecutionRequest
        {
            Machine = machine,
            ScriptBody = scriptBody,
            ExecutionMode = ExecutionMode.DirectScript,
            Syntax = ScriptSyntax.Bash,
            Files = new Dictionary<string, byte[]>(),
            Variables = new List<Message.Models.Deployments.Variable.VariableDto>()
        };

        Log.Information("Running health check for machine {MachineName} ({Style})", machine.Name, style);

        var result = await transport.Strategy.ExecuteScriptAsync(request, cancellationToken).ConfigureAwait(false);

        var status = result.Success ? MachineHealthStatus.Healthy : MachineHealthStatus.Unhealthy;
        var detail = string.Join("\n", result.LogLines ?? new List<string>());

        await RecordHealthStatusAsync(machine, status, detail, cancellationToken).ConfigureAwait(false);

        Log.Information("Health check for {MachineName}: {Status}", machine.Name, status);
    }

    private async Task<string> ResolveHealthCheckScriptAsync(Machine machine, CommunicationStyle style, CancellationToken cancellationToken)
    {
        if (machine.MachinePolicyId == null) return DefaultHealthCheckScript;

        var policy = await _policyDataProvider.GetByIdAsync(machine.MachinePolicyId.Value, cancellationToken).ConfigureAwait(false);

        if (policy == null) return DefaultHealthCheckScript;

        var healthCheckPolicy = DeserializePolicy(policy.MachineHealthCheckPolicy);

        if (healthCheckPolicy == null) return DefaultHealthCheckScript;

        var styleKey = style.ToString();

        if (!healthCheckPolicy.ScriptPolicies.TryGetValue(styleKey, out var scriptPolicy)) return DefaultHealthCheckScript;

        if (scriptPolicy.RunType == "InheritFromDefault" || string.IsNullOrWhiteSpace(scriptPolicy.ScriptBody)) return DefaultHealthCheckScript;

        return scriptPolicy.ScriptBody;
    }

    private async Task RecordHealthStatusAsync(Machine machine, MachineHealthStatus status, string detail, CancellationToken cancellationToken)
    {
        machine.HealthStatus = status;
        machine.HealthLastChecked = DateTime.UtcNow;
        machine.HealthDetailJson = detail;

        await _machineDataProvider.UpdateMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);
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
