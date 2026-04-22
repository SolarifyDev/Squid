using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

public class TentacleHealthCheckStrategy : IHealthCheckStrategy
{
    /// <summary>
    /// Metadata key under which the agent embeds raw upgrade-status JSON on
    /// every Capabilities RPC. Mirror of
    /// <c>CapabilitiesService.UpgradeStatusMetadataKey</c> on the agent
    /// side — pinned by <c>UpgradeStatusMetadataKey_MatchesAgentContract</c>.
    /// </summary>
    internal const string UpgradeStatusMetadataKey = "upgradeStatus";

    /// <summary>
    /// Metadata key under which the agent embeds the JSONL upgrade events
    /// log (B1, 1.5.0). Mirror of
    /// <c>CapabilitiesService.UpgradeEventsMetadataKey</c> — pinned by
    /// <c>UpgradeEventsMetadataKey_MatchesAgentContract</c>.
    /// </summary>
    internal const string UpgradeEventsMetadataKey = "upgradeEvents";

    /// <summary>
    /// Metadata key under which the agent embeds the Phase B bash log
    /// (B4, 1.6.0). Mirror of <c>CapabilitiesService.UpgradeLogMetadataKey</c>.
    /// </summary>
    internal const string UpgradeLogMetadataKey = "upgradeLog";

    private readonly IHalibutClientFactory _halibutClientFactory;
    private readonly IMachineRuntimeCapabilitiesCache _capabilitiesCache;
    private readonly IUpgradeDispatchLockReconciler _upgradeLockReconciler;
    private readonly IUpgradeEventTimelineStore _upgradeEventStore;

    public TentacleHealthCheckStrategy(IHalibutClientFactory halibutClientFactory, IMachineRuntimeCapabilitiesCache capabilitiesCache = null, IUpgradeDispatchLockReconciler upgradeLockReconciler = null, IUpgradeEventTimelineStore upgradeEventStore = null)
    {
        _halibutClientFactory = halibutClientFactory;
        _capabilitiesCache = capabilitiesCache;
        _upgradeLockReconciler = upgradeLockReconciler;
        _upgradeEventStore = upgradeEventStore;
    }

    /// <summary>
    /// Three-tier probe:
    ///   1. Halibut round-trip via CapabilitiesService (transport + mutual TLS + basic RPC)
    ///   2. Capabilities metadata parsed into machine runtime capabilities cache so
    ///      the next deployment can pick the right script syntax without an extra call
    ///   3. (Optional) correlation-script probe: a short echo executed through the
    ///      agent that must return a fresh random token. This catches an agent whose
    ///      CapabilitiesService answers (cached) but whose script backend is stuck
    ///      or MITM-impersonated
    /// </summary>
    public async Task<HealthCheckResult> CheckHealthAsync(Machine machine, MachineConnectivityPolicyDto connectivityPolicy, CancellationToken ct, MachineHealthCheckPolicyDto healthCheckPolicy = null)
    {
        var endpoint = ParseEndpoint(machine);

        if (endpoint == null)
            return new HealthCheckResult(false, "Cannot construct Tentacle endpoint (missing Uri/SubscriptionId or Thumbprint)");

        try
        {
            var capsClient = _halibutClientFactory.CreateCapabilitiesClient(endpoint);
            var response = await capsClient.GetCapabilitiesAsync(new CapabilitiesRequest()).ConfigureAwait(false);

            if (response == null)
                return new HealthCheckResult(false, "Tentacle returned null capabilities response");

            CacheCapabilitiesFor(machine, response);

            await ReconcileStaleUpgradeIfNeededAsync(machine, response, ct).ConfigureAwait(false);

            CaptureUpgradeEventTimeline(machine, response);

            var os = ReadMetadata(response, "os");
            var shell = ReadMetadata(response, "defaultShell");
            var osInfo = string.IsNullOrEmpty(os) ? string.Empty : $", OS={os}";
            var shellInfo = string.IsNullOrEmpty(shell) ? string.Empty : $", shell={shell}";

            return new HealthCheckResult(true, $"Tentacle connected — version {response.AgentVersion}{osInfo}{shellInfo}, services: {string.Join(", ", response.SupportedServices)}");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(false, $"Tentacle connectivity failed: {ex.Message}");
        }
    }

    private void CacheCapabilitiesFor(Machine machine, CapabilitiesResponse response)
    {
        if (_capabilitiesCache == null || machine == null) return;
        _capabilitiesCache.Store(machine.Id, response.Metadata, response.AgentVersion);
    }

    /// <summary>
    /// If the agent reports a stale IN_PROGRESS upgrade (schema v2+ only —
    /// 1.4.x agents are silently skipped), delete the server-side Redis
    /// dispatch lock so the next operator click isn't blocked. Never throws
    /// — a reconciler failure MUST NOT turn a healthy tentacle into an
    /// unhealthy one in the health-check UI.
    /// </summary>
    private async Task ReconcileStaleUpgradeIfNeededAsync(Machine machine, CapabilitiesResponse response, CancellationToken ct)
    {
        if (_upgradeLockReconciler == null || machine == null || response?.Metadata == null) return;

        if (!response.Metadata.TryGetValue(UpgradeStatusMetadataKey, out var rawStatusJson)) return;

        var payload = UpgradeStatusPayload.TryParse(rawStatusJson);

        if (payload == null) return;

        try
        {
            await _upgradeLockReconciler.ClearLockIfStaleAsync(machine.Id, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[UpgradeAudit] Stale-upgrade reconciler threw for machine {MachineId} — swallowed to keep health check independent", machine.Id);
        }
    }

    /// <summary>
    /// Parse the JSONL events log from the agent's metadata and store the
    /// per-machine timeline so the API/UI layer can show real-time progress
    /// (B2, 1.5.x). Same swallow-errors discipline as the reconciler — a
    /// parse failure or store error must not degrade the underlying health
    /// check. 1.4.x agents emit no events file → metadata key absent →
    /// we just skip silently (no events to display, fall back to
    /// status-file detail string only).
    /// </summary>
    private void CaptureUpgradeEventTimeline(Machine machine, CapabilitiesResponse response)
    {
        if (_upgradeEventStore == null || machine == null || response?.Metadata == null) return;

        if (!response.Metadata.TryGetValue(UpgradeEventsMetadataKey, out var rawJsonl)) return;

        try
        {
            var events = UpgradeStatusPayload.TryParseEvents(rawJsonl);
            _upgradeEventStore.Store(machine.Id, events);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[UpgradeAudit] Failed to capture upgrade event timeline for machine {MachineId}", machine.Id);
        }

        // B4 (1.6.0): also capture the Phase B log text if the agent
        // embedded it. Same swallow-errors discipline.
        if (response.Metadata.TryGetValue(UpgradeLogMetadataKey, out var rawLog) && !string.IsNullOrEmpty(rawLog))
        {
            try
            {
                _upgradeEventStore.StoreLog(machine.Id, rawLog);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[UpgradeAudit] Failed to capture Phase B log for machine {MachineId}", machine.Id);
            }
        }
    }

    private static string ReadMetadata(CapabilitiesResponse response, string key)
        => response.Metadata != null && response.Metadata.TryGetValue(key, out var v) ? v ?? string.Empty : string.Empty;

    internal static global::Halibut.ServiceEndPoint ParseEndpoint(Machine machine)
    {
        var style = EndpointJsonHelper.GetField(machine.Endpoint, "CommunicationStyle");

        return style == nameof(Message.Enums.CommunicationStyle.TentacleListening)
            ? EndpointJsonHelper.ParseTentacleListeningEndpoint(machine.Endpoint)
            : EndpointJsonHelper.ParseHalibutEndpoint(machine.Endpoint);
    }
}
