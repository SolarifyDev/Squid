using System.Text.Json;
using Squid.Core.Halibut;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines.Exceptions;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Events.Machine;
using Squid.Message.Json;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Services.Machines;

public interface IMachineService : IScopedDependency
{
    Task<GetMachinesResponse> GetMachinesAsync(GetMachinesRequest request, CancellationToken cancellationToken);

    Task<UpdateMachineResponse> UpdateMachineAsync(UpdateMachineCommand command, CancellationToken cancellationToken);

    Task<MachineDeletedEvent> DeleteMachinesAsync(DeleteMachinesCommand command, CancellationToken cancellationToken);
}

public class MachineService : IMachineService
{
    private readonly IMapper _mapper;
    private readonly IMachineDataProvider _machineDataProvider;
    private readonly IPollingTrustDistributor _trustDistributor;
    private readonly IMachineRuntimeCapabilitiesCache _runtimeCache;

    public MachineService(IMapper mapper, IMachineDataProvider machineDataProvider, IPollingTrustDistributor trustDistributor, IMachineRuntimeCapabilitiesCache runtimeCache)
    {
        _mapper = mapper;
        _machineDataProvider = machineDataProvider;
        _trustDistributor = trustDistributor;
        _runtimeCache = runtimeCache;
    }

    public async Task<GetMachinesResponse> GetMachinesAsync(GetMachinesRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await _machineDataProvider.GetMachinePagingAsync(
            request.SpaceId, request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        var machines = _mapper.Map<List<MachineDto>>(data);

        EnrichWithRuntimeCache(machines);

        return new GetMachinesResponse
        {
            Data = new GetMachinesResponseData
            {
                Count = count,
                Machines = machines
            }
        };
    }

    /// <summary>
    /// Post-map enrichment: AutoMapper profiles are stateless, but the
    /// agent version lives in <see cref="IMachineRuntimeCapabilitiesCache"/>
    /// (populated by health-check Capabilities probes). Populating here
    /// keeps the mapping profile pure while still giving the UI a
    /// per-row agent version for "upgrade available" badges.
    ///
    /// <para>Empty string on cache miss (machine never health-checked) —
    /// the frontend treats empty as "version unknown" and hides the
    /// upgrade-available badge rather than guessing.</para>
    /// </summary>
    private void EnrichWithRuntimeCache(List<MachineDto> machines)
    {
        foreach (var dto in machines)
            dto.AgentVersion = _runtimeCache.TryGet(dto.Id).AgentVersion ?? string.Empty;
    }

    private void EnrichWithRuntimeCache(MachineDto machine)
    {
        machine.AgentVersion = _runtimeCache.TryGet(machine.Id).AgentVersion ?? string.Empty;
    }

    public async Task<UpdateMachineResponse> UpdateMachineAsync(UpdateMachineCommand command, CancellationToken cancellationToken)
    {
        var machine = await _machineDataProvider.GetMachinesByIdAsync(command.MachineId, cancellationToken).ConfigureAwait(false);

        if (machine == null) throw new MachineNotFoundException(command.MachineId);

        await EnsureNameAvailableIfChangedAsync(machine, command, cancellationToken).ConfigureAwait(false);

        // Parse style once — cheap but not free (JsonDocument.Parse on the
        // endpoint JSON); reused by both the validator and the dispatcher.
        var style = CommunicationStyleParser.Parse(machine.Endpoint);

        // Validate BEFORE mutating (throws MachineEndpointUpdateNotApplicableException
        // on cross-style contamination) AND report whether any style-specific
        // field was actually set — lets us skip the endpoint-JSON round-trip
        // entirely on rename-only / role-only updates.
        var hasStyleFieldToApply = EnsureCommandFieldsBelongToMachineStyle(machine.Id, style, command);

        ApplyCommonFields(machine, command);

        if (hasStyleFieldToApply) ApplyEndpointUpdate(machine, style, command);

        await _machineDataProvider.UpdateMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

        _trustDistributor.Reconfigure();

        Log.Information("Updated machine {MachineName} (Id={MachineId})", machine.Name, machine.Id);

        var dto = _mapper.Map<MachineDto>(machine);
        EnrichWithRuntimeCache(dto);

        return new UpdateMachineResponse { Data = dto };
    }

    private async Task EnsureNameAvailableIfChangedAsync(Persistence.Entities.Deployments.Machine machine, UpdateMachineCommand command, CancellationToken ct)
    {
        if (command.Name == null || string.Equals(command.Name, machine.Name, StringComparison.Ordinal)) return;

        var spaceId = command.SpaceId ?? machine.SpaceId;

        if (await _machineDataProvider.ExistsByNameAsync(command.Name, spaceId, ct).ConfigureAwait(false))
            throw new MachineNameConflictException(command.Name, spaceId);
    }

    private static void ApplyCommonFields(Persistence.Entities.Deployments.Machine machine, UpdateMachineCommand c)
    {
        if (c.Name != null) machine.Name = c.Name;
        if (c.IsDisabled.HasValue) machine.IsDisabled = c.IsDisabled.Value;
        if (c.Roles != null) machine.Roles = JsonSerializer.Serialize(c.Roles);
        if (c.EnvironmentIds != null) machine.EnvironmentIds = JsonSerializer.Serialize(c.EnvironmentIds);
        if (c.MachinePolicyId.HasValue) machine.MachinePolicyId = c.MachinePolicyId.Value;
    }

    // ── Endpoint update ──────────────────────────────────────────────────────
    //
    // Metadata update only — NOT to be confused with MachineUpgradeService,
    // which does the real "upgrade the agent binary" work via Halibut RPC.
    // This path just edits the endpoint JSON column: rename, rotate thumbprint,
    // change SSH host, etc. One algorithm ("deserialise → merge non-null
    // fields → re-serialise") with per-style DTO shape; no strategy pattern
    // needed — that ceremony belongs to the upgrade flow where each platform
    // is a genuinely different algorithm (bash script vs helm vs PowerShell).

    /// <summary>
    /// Rejects any request where the command carries a field that doesn't
    /// belong to the target machine's CommunicationStyle, AND reports
    /// whether any legal style-specific field was set. The bool return
    /// is the by-product that lets us skip the endpoint-JSON round-trip
    /// on rename-only / role-only updates — one walk of the field list
    /// instead of two.
    ///
    /// <para>Throws <see cref="MachineEndpointUpdateNotApplicableException"/>
    /// on the first cross-style field it finds. No mutation happens
    /// before throwing, so no cleanup / transaction is needed.</para>
    /// </summary>
    /// <returns><see langword="true"/> iff the command set at least one
    /// field that legitimately applies to this machine's style — caller
    /// uses this to gate the endpoint-JSON deserialise/merge/serialise cycle.</returns>
    private static bool EnsureCommandFieldsBelongToMachineStyle(int machineId, CommunicationStyle style, UpdateMachineCommand c)
    {
        var anyStyleFieldSet = false;

        void Check(bool isSet, string fieldName, params CommunicationStyle[] allowed)
        {
            if (!isSet) return;

            if (!allowed.Contains(style))
                throw new MachineEndpointUpdateNotApplicableException(
                    machineId: machineId,
                    machineStyle: style.ToString(),
                    offendingField: fieldName,
                    acceptedForStyles: string.Join(", ", allowed.Select(s => s.ToString())));

            anyStyleFieldSet = true;
        }

        // One declarative table — each row names a field, its is-set
        // predicate, and the styles that may carry it. Adding a new
        // style-specific field to UpdateMachineCommand ⇒ one new row.
        Check(c.ClusterUrl != null, nameof(c.ClusterUrl), CommunicationStyle.KubernetesApi);
        Check(c.Namespace != null, nameof(c.Namespace), CommunicationStyle.KubernetesApi, CommunicationStyle.KubernetesAgent);
        Check(c.SkipTlsVerification.HasValue, nameof(c.SkipTlsVerification), CommunicationStyle.KubernetesApi);
        Check(c.ProviderType.HasValue, nameof(c.ProviderType), CommunicationStyle.KubernetesApi);
        Check(c.ProviderConfig != null, nameof(c.ProviderConfig), CommunicationStyle.KubernetesApi);

        Check(c.ReleaseName != null, nameof(c.ReleaseName), CommunicationStyle.KubernetesAgent);
        Check(c.HelmNamespace != null, nameof(c.HelmNamespace), CommunicationStyle.KubernetesAgent);
        Check(c.ChartRef != null, nameof(c.ChartRef), CommunicationStyle.KubernetesAgent);

        Check(c.SubscriptionId != null, nameof(c.SubscriptionId), CommunicationStyle.TentaclePolling, CommunicationStyle.KubernetesAgent);
        Check(c.Thumbprint != null, nameof(c.Thumbprint), CommunicationStyle.TentaclePolling, CommunicationStyle.TentacleListening, CommunicationStyle.KubernetesAgent);

        Check(c.Uri != null, nameof(c.Uri), CommunicationStyle.TentacleListening);
        Check(c.ProxyId.HasValue, nameof(c.ProxyId), CommunicationStyle.TentacleListening);

        Check(c.BaseUrl != null, nameof(c.BaseUrl), CommunicationStyle.OpenClaw);
        Check(c.InlineGatewayToken != null, nameof(c.InlineGatewayToken), CommunicationStyle.OpenClaw);
        Check(c.InlineHooksToken != null, nameof(c.InlineHooksToken), CommunicationStyle.OpenClaw);

        Check(c.Host != null, nameof(c.Host), CommunicationStyle.Ssh);
        Check(c.Port.HasValue, nameof(c.Port), CommunicationStyle.Ssh);
        Check(c.Fingerprint != null, nameof(c.Fingerprint), CommunicationStyle.Ssh);
        Check(c.RemoteWorkingDirectory != null, nameof(c.RemoteWorkingDirectory), CommunicationStyle.Ssh);
        Check(c.ProxyType.HasValue, nameof(c.ProxyType), CommunicationStyle.Ssh);
        Check(c.ProxyHost != null, nameof(c.ProxyHost), CommunicationStyle.Ssh);
        Check(c.ProxyPort.HasValue, nameof(c.ProxyPort), CommunicationStyle.Ssh);
        Check(c.ProxyUsername != null, nameof(c.ProxyUsername), CommunicationStyle.Ssh);
        Check(c.ProxyPassword != null, nameof(c.ProxyPassword), CommunicationStyle.Ssh);

        Check(c.ResourceReferences != null, nameof(c.ResourceReferences),
            CommunicationStyle.KubernetesApi, CommunicationStyle.OpenClaw, CommunicationStyle.Ssh);

        return anyStyleFieldSet;
    }

    /// <summary>
    /// Per-style endpoint JSON merge. One method per DTO shape because the
    /// type parameter varies; the body is a homogeneous
    /// <c>endpoint.X = command.X ?? endpoint.X</c> ladder — any field the
    /// command doesn't set is preserved from the existing endpoint JSON.
    /// </summary>
    private static void ApplyEndpointUpdate(Persistence.Entities.Deployments.Machine machine, CommunicationStyle style, UpdateMachineCommand c)
    {
        machine.Endpoint = style switch
        {
            CommunicationStyle.KubernetesApi => MergeKubernetesApi(machine.Endpoint, c),
            CommunicationStyle.KubernetesAgent => MergeKubernetesAgent(machine.Endpoint, c),
            CommunicationStyle.OpenClaw => MergeOpenClaw(machine.Endpoint, c),
            CommunicationStyle.Ssh => MergeSsh(machine.Endpoint, c),
            CommunicationStyle.TentaclePolling => MergeTentaclePolling(machine.Endpoint, c),
            CommunicationStyle.TentacleListening => MergeTentacleListening(machine.Endpoint, c),
            _ => machine.Endpoint   // validator already blocked style-field contamination; nothing to merge
        };
    }

    private static string MergeKubernetesApi(string json, UpdateMachineCommand c)
    {
        var e = Deserialise<KubernetesApiEndpointDto>(json);
        e.ClusterUrl = c.ClusterUrl ?? e.ClusterUrl;
        e.Namespace = c.Namespace ?? e.Namespace;
        e.SkipTlsVerification = c.SkipTlsVerification?.ToString() ?? e.SkipTlsVerification;
        e.ProviderType = c.ProviderType ?? e.ProviderType;
        e.ProviderConfig = c.ProviderConfig ?? e.ProviderConfig;
        e.ResourceReferences = c.ResourceReferences ?? e.ResourceReferences;
        return JsonSerializer.Serialize(e);
    }

    private static string MergeKubernetesAgent(string json, UpdateMachineCommand c)
    {
        var e = Deserialise<KubernetesAgentEndpointDto>(json);
        e.SubscriptionId = c.SubscriptionId ?? e.SubscriptionId;
        e.Thumbprint = c.Thumbprint ?? e.Thumbprint;
        e.Namespace = c.Namespace ?? e.Namespace;
        e.ReleaseName = c.ReleaseName ?? e.ReleaseName;
        e.HelmNamespace = c.HelmNamespace ?? e.HelmNamespace;
        e.ChartRef = c.ChartRef ?? e.ChartRef;
        return JsonSerializer.Serialize(e);
    }

    private static string MergeOpenClaw(string json, UpdateMachineCommand c)
    {
        var e = Deserialise<OpenClawEndpointDto>(json);
        e.BaseUrl = c.BaseUrl ?? e.BaseUrl;
        e.InlineGatewayToken = c.InlineGatewayToken ?? e.InlineGatewayToken;
        e.InlineHooksToken = c.InlineHooksToken ?? e.InlineHooksToken;
        e.ResourceReferences = c.ResourceReferences ?? e.ResourceReferences;
        return JsonSerializer.Serialize(e);
    }

    private static string MergeSsh(string json, UpdateMachineCommand c)
    {
        var e = Deserialise<SshEndpointDto>(json);
        e.Host = c.Host ?? e.Host;
        e.Port = c.Port ?? e.Port;
        e.Fingerprint = c.Fingerprint ?? e.Fingerprint;
        e.RemoteWorkingDirectory = c.RemoteWorkingDirectory ?? e.RemoteWorkingDirectory;
        e.ProxyType = c.ProxyType ?? e.ProxyType;
        e.ProxyHost = c.ProxyHost ?? e.ProxyHost;
        e.ProxyPort = c.ProxyPort ?? e.ProxyPort;
        e.ProxyUsername = c.ProxyUsername ?? e.ProxyUsername;
        e.ProxyPassword = c.ProxyPassword ?? e.ProxyPassword;
        e.ResourceReferences = c.ResourceReferences ?? e.ResourceReferences;
        return JsonSerializer.Serialize(e);
    }

    private static string MergeTentaclePolling(string json, UpdateMachineCommand c)
    {
        var e = Deserialise<TentaclePollingEndpointDto>(json);
        e.SubscriptionId = c.SubscriptionId ?? e.SubscriptionId;
        e.Thumbprint = c.Thumbprint ?? e.Thumbprint;
        return JsonSerializer.Serialize(e);
    }

    private static string MergeTentacleListening(string json, UpdateMachineCommand c)
    {
        var e = Deserialise<TentacleListeningEndpointDto>(json);
        e.Uri = c.Uri ?? e.Uri;
        e.Thumbprint = c.Thumbprint ?? e.Thumbprint;
        e.ProxyId = c.ProxyId ?? e.ProxyId;
        return JsonSerializer.Serialize(e);
    }

    private static T Deserialise<T>(string json) where T : class, new()
        => !string.IsNullOrEmpty(json)
            ? JsonSerializer.Deserialize<T>(json, SquidJsonDefaults.CaseInsensitive) ?? new T()
            : new T();

    public async Task<MachineDeletedEvent> DeleteMachinesAsync(DeleteMachinesCommand command, CancellationToken cancellationToken)
    {
        var machines = await _machineDataProvider.GetMachinesByIdsAsync(command.Ids, cancellationToken).ConfigureAwait(false);

        await _machineDataProvider.DeleteMachinesAsync(machines, cancellationToken: cancellationToken).ConfigureAwait(false);

        _trustDistributor.Reconfigure();

        var deletedIds = machines.Select(m => m.Id).ToList();
        var failIds = command.Ids.Except(deletedIds).ToList();

        Log.Information("Deleted {Count} machines: {Ids}", deletedIds.Count, deletedIds);

        return new MachineDeletedEvent
        {
            Data = new DeleteMachinesResponseData
            {
                FailIds = failIds
            }
        };
    }
}
