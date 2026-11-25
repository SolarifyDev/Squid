using Halibut;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Settings.Halibut;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

/// <summary>
/// Keeps this pod's picture of what an agent speaks current, from two sources: the persisted copy other
/// pods write (cheap, before a dispatch) and the agent itself (bounded, after a call over a chosen
/// protocol version failed). Separate from <see cref="IScriptDispatcherSelector"/>, which stays a pure
/// function of what is cached.
/// </summary>
public interface IMachineCapabilitiesRefresher : IScopedDependency
{
    /// <summary>
    /// Adopts the persisted snapshot when it is newer than this pod's entry — a health check on another pod,
    /// or an upgrade/downgrade that dropped the snapshot (evicting the local entry). Never throws: a failed
    /// read leaves the cache as it is, which is what every dispatch did before.
    /// </summary>
    Task SyncFromStoreAsync(int machineId, CancellationToken ct);

    /// <summary>
    /// Asks the agent for its capabilities within <see cref="CapabilitiesSettings.RefreshTimeout"/>, records
    /// the answer and returns it; <see langword="null"/> when the agent did not answer in time or failed, in
    /// which case nothing about its protocol can be concluded.
    /// </summary>
    Task<MachineRuntimeCapabilities> RefreshFromAgentAsync(int machineId, ServiceEndPoint endpoint, CancellationToken ct);

    /// <summary>The longest <see cref="RefreshFromAgentAsync"/> waits for the agent: what a caller sharing a deadline with it must leave room for.</summary>
    TimeSpan RefreshTimeout { get; }
}

public sealed class MachineCapabilitiesRefresher : IMachineCapabilitiesRefresher
{
    private readonly IHalibutClientFactory _clientFactory;
    private readonly IMachineRuntimeCapabilitiesCache _cache;
    private readonly IMachineCapabilitiesStoreAccess _store;
    private readonly IMachineCapabilitiesRecorder _recorder;
    private readonly CapabilitiesSettings _settings;

    public MachineCapabilitiesRefresher(IHalibutClientFactory clientFactory, IMachineRuntimeCapabilitiesCache cache, IMachineCapabilitiesStoreAccess store, IMachineCapabilitiesRecorder recorder, HalibutSetting halibutSetting)
    {
        _clientFactory = clientFactory;
        _cache = cache;
        _store = store;
        _recorder = recorder;
        _settings = halibutSetting.Capabilities ?? new CapabilitiesSettings();
    }

    public TimeSpan RefreshTimeout => _settings.RefreshTimeout;

    public async Task SyncFromStoreAsync(int machineId, CancellationToken ct)
    {
        try
        {
            var observedAt = _cache.TryGet(machineId).ObservedAt;
            var snapshot = await _store.RunAsync(persistence => persistence.LoadIfNewerAsync(machineId, observedAt, ct)).ConfigureAwait(false);

            if (snapshot != null) Adopt(machineId, snapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Could not compare persisted runtime capabilities for machine {MachineId}; dispatching from this pod's cache.", machineId);
        }
    }

    private void Adopt(int machineId, PersistedCapabilitiesSnapshot snapshot)
    {
        if (snapshot.Capabilities == null)
        {
            _cache.Invalidate(machineId);
            return;
        }

        var c = snapshot.Capabilities;

        _cache.Store(machineId, MachineRuntimeCapabilitiesCacheHydrator.BuildMetadataDictionary(c), c.AgentVersion, c.SupportedServices, snapshot.UpdatedAt);
    }

    public async Task<MachineRuntimeCapabilities> RefreshFromAgentAsync(int machineId, ServiceEndPoint endpoint, CancellationToken ct)
    {
        try
        {
            var probe = _clientFactory.CreateCapabilitiesClient(endpoint).GetCapabilitiesAsync(new CapabilitiesRequest());
            var response = await probe.WaitAsync(_settings.RefreshTimeout, ct).ConfigureAwait(false);

            return response == null ? null : await _recorder.RecordAsync(machineId, response, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning(ex, "Could not refresh capabilities from the agent of machine {MachineId}; nothing can be concluded about its protocol versions.", machineId);

            return null;
        }
    }
}
