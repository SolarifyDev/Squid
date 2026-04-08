using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums;
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

        if (transport?.HealthChecker == null)
        {
            await RecordUnavailableAsync(machine, $"No health checker for {CommunicationStyleParser.Parse(machine.Endpoint)}", cancellationToken).ConfigureAwait(false);
            return;
        }

        var connectivityPolicy = await LoadConnectivityPolicyAsync(machine, cancellationToken).ConfigureAwait(false);
        var healthCheckPolicy = await LoadHealthCheckPolicyAsync(machine, cancellationToken).ConfigureAwait(false);

        await RunAndRecordAsync(machine, transport, connectivityPolicy, healthCheckPolicy, cancellationToken).ConfigureAwait(false);
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

                var transport = ResolveTransport(machine);

                if (transport?.HealthChecker == null)
                {
                    await RecordUnavailableAsync(machine, $"No health checker for {CommunicationStyleParser.Parse(machine.Endpoint)}", cancellationToken).ConfigureAwait(false);
                    checkedCount++;
                    continue;
                }

                var connectivityPolicy = await LoadConnectivityPolicyAsync(machine, cancellationToken).ConfigureAwait(false);

                await RunAndRecordAsync(machine, transport, connectivityPolicy, healthCheckPolicy, cancellationToken).ConfigureAwait(false);
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
    // Core — single entry point, strategy decides how
    // ========================================================================

    private async Task RunAndRecordAsync(Machine machine, IDeploymentTransport transport, MachineConnectivityPolicyDto connectivityPolicy, MachineHealthCheckPolicyDto healthCheckPolicy, CancellationToken cancellationToken)
    {
        Log.Information("Running health check for machine {MachineName} ({Style})", machine.Name, transport.CommunicationStyle);

        var result = await transport.HealthChecker.CheckHealthAsync(machine, connectivityPolicy, cancellationToken, healthCheckPolicy).ConfigureAwait(false);
        var status = result.Healthy ? MachineHealthStatus.Healthy : MachineHealthStatus.Unavailable;

        await RecordHealthStatusAsync(machine, status, result.Detail, cancellationToken).ConfigureAwait(false);

        Log.Information("Health check for {MachineName}: {Status}", machine.Name, status);
    }

    // ========================================================================
    // Infrastructure
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
        return _transportRegistry.Resolve(style);
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
    // Schedule helpers
    // ========================================================================

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

    private async Task<MachineHealthCheckPolicyDto> LoadHealthCheckPolicyAsync(Machine machine, CancellationToken cancellationToken)
    {
        if (machine.MachinePolicyId == null) return null;

        var policy = await _policyDataProvider.GetByIdAsync(machine.MachinePolicyId.Value, cancellationToken).ConfigureAwait(false);

        if (policy == null) return null;

        return DeserializePolicy<MachineHealthCheckPolicyDto>(policy.MachineHealthCheckPolicy);
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
