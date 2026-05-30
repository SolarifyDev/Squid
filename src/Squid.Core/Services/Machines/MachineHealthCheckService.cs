using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Machines;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.Machines;

public interface IMachineHealthCheckService : IScopedDependency
{
    /// <summary>
    /// Active health-check probe — runs the per-style <c>IHealthCheckStrategy</c>
    /// (typically a Halibut Capabilities RPC for tentacles) and returns a
    /// structured outcome. Updates the capability cache + DB persistence
    /// (H2) atomically with the probe result.
    ///
    /// <para>H3 — return type changed from <c>Task</c> to
    /// <c>Task&lt;ManualHealthCheckResult&gt;</c> so FE / CLI can render
    /// "agent_unreachable" vs "agent ok, here are the new capabilities" without
    /// re-parsing the human-readable detail. Existing callers that ignore the
    /// result (<c>await svc.ManualHealthCheckAsync(...)</c>) are unaffected —
    /// the awaitable contract is preserved.</para>
    /// </summary>
    Task<ManualHealthCheckResult> ManualHealthCheckAsync(int machineId, CancellationToken cancellationToken = default);

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
    private readonly IMachineRuntimeCapabilitiesCache _capabilitiesCache;

    public MachineHealthCheckService(IMachineDataProvider machineDataProvider, IMachinePolicyDataProvider policyDataProvider, ITransportRegistry transportRegistry, IMachineRuntimeCapabilitiesCache capabilitiesCache = null)
    {
        _machineDataProvider = machineDataProvider;
        _policyDataProvider = policyDataProvider;
        _transportRegistry = transportRegistry;
        _capabilitiesCache = capabilitiesCache;
    }

    public async Task<ManualHealthCheckResult> ManualHealthCheckAsync(int machineId, CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTimeOffset.UtcNow;

        var machine = await _machineDataProvider.GetMachinesByIdAsync(machineId, cancellationToken).ConfigureAwait(false);

        if (machine == null)
            return new ManualHealthCheckResult
            {
                Successful = false,
                Detail = $"Machine {machineId} not found",
                ErrorCode = ManualHealthCheckErrorCodes.MachineNotFound,
                CheckedAt = checkedAt
            };

        if (machine.IsDisabled)
        {
            Log.Information("Skipping health check for disabled machine {MachineName}", machine.Name);
            return new ManualHealthCheckResult
            {
                Successful = false,
                Detail = $"Machine '{machine.Name}' is disabled — enable it before running a health check.",
                ErrorCode = ManualHealthCheckErrorCodes.MachineDisabled,
                CheckedAt = checkedAt
            };
        }

        var transport = ResolveTransport(machine);

        if (transport?.HealthChecker == null)
        {
            var detail = $"No health checker for {CommunicationStyleParser.Parse(machine.Endpoint)}";
            await RecordUnavailableAsync(machine, detail, cancellationToken).ConfigureAwait(false);
            return new ManualHealthCheckResult
            {
                Successful = false,
                Detail = detail,
                ErrorCode = ManualHealthCheckErrorCodes.NoHealthChecker,
                CheckedAt = checkedAt
            };
        }

        var connectivityPolicy = await LoadConnectivityPolicyAsync(machine, cancellationToken).ConfigureAwait(false);
        var healthCheckPolicy = await LoadHealthCheckPolicyAsync(machine, cancellationToken).ConfigureAwait(false);

        var probeResult = await RunAndRecordAsync(machine, transport, connectivityPolicy, healthCheckPolicy, cancellationToken).ConfigureAwait(false);

        // Read cache POST-probe so the FE sees the freshly-written capabilities
        // (TentacleHealthCheckStrategy.CacheCapabilitiesFor writes during the
        // probe). If the cache miss (e.g. non-tentacle transport that doesn't
        // populate capabilities), AgentVersion / Os stay empty — that's fine,
        // the success flag + detail give the caller enough signal.
        var caps = _capabilitiesCache?.TryGet(machine.Id);

        return new ManualHealthCheckResult
        {
            Successful = probeResult.Healthy,
            Detail = probeResult.Detail ?? string.Empty,
            ErrorCode = ResolveUnreachableErrorCode(probeResult.Healthy, connectivityPolicy),
            AgentVersion = caps?.AgentVersion ?? string.Empty,
            Os = caps?.Os ?? string.Empty,
            CheckedAt = checkedAt
        };
    }

    // A reachable probe has no error code. An unreachable one is a hard
    // agent_unreachable unless the machine policy's connectivity behaviour tolerates
    // offline targets (MayBeOfflineAndCanBeSkipped), in which case it is reported as
    // the benign offline_tolerated. Default / no policy → agent_unreachable (unchanged).
    private static string ResolveUnreachableErrorCode(bool healthy, MachineConnectivityPolicyDto connectivityPolicy)
    {
        if (healthy) return null;

        return MachineConnectivityEvaluator.AllowsOffline(connectivityPolicy)
            ? ManualHealthCheckErrorCodes.OfflineTolerated
            : ManualHealthCheckErrorCodes.AgentUnreachable;
    }

    public async Task AutoHealthCheckForAllAsync(CancellationToken cancellationToken = default)
    {
        // P1-D.8 (Phase-8): the health-check sweep is system-internal (runs
        // under InternalUser / Hangfire) and legitimately scans every space.
        // Use the explicitly-named cross-space method instead of the
        // (now obsolete) nullable-spaceId entry — makes the cross-space
        // intent visible at the call site.
        var (_, machines) = await _machineDataProvider.GetMachinesAllSpacesPagingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

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

    private async Task<HealthCheckResult> RunAndRecordAsync(Machine machine, IDeploymentTransport transport, MachineConnectivityPolicyDto connectivityPolicy, MachineHealthCheckPolicyDto healthCheckPolicy, CancellationToken cancellationToken)
    {
        Log.Information("Running health check for machine {MachineName} ({Style})", machine.Name, transport.CommunicationStyle);

        var result = await transport.HealthChecker.CheckHealthAsync(machine, connectivityPolicy, cancellationToken, healthCheckPolicy).ConfigureAwait(false);
        var status = result.Healthy ? MachineHealthStatus.Healthy : MachineHealthStatus.Unavailable;

        await RecordHealthStatusAsync(machine, status, result.Detail, cancellationToken).ConfigureAwait(false);

        Log.Information("Health check for {MachineName}: {Status}", machine.Name, status);

        return result;
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
        var now = DateTimeOffset.UtcNow;

        machine.UnavailableSince = ResolveUnavailableSince(machine.HealthStatus, machine.UnavailableSince, status, now);
        machine.HealthStatus = status;
        machine.HealthLastChecked = now;
        machine.HealthDetail = detail;

        await _machineDataProvider.UpdateMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // Track the FIRST instant a machine became Unavailable: stamp it on entry,
    // preserve it while the machine stays Unavailable, clear it once the machine
    // recovers to any non-Unavailable status. Pure so the transition is
    // unit-testable and the cleanup grace period measures continuous downtime.
    internal static DateTimeOffset? ResolveUnavailableSince(MachineHealthStatus previous, DateTimeOffset? previousUnavailableSince, MachineHealthStatus next, DateTimeOffset now)
    {
        if (next != MachineHealthStatus.Unavailable) return null;

        return previous == MachineHealthStatus.Unavailable ? previousUnavailableSince : now;
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
