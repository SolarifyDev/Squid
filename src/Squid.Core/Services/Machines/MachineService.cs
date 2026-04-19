using System.Text.Json;
using Squid.Core.Halibut;
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

    public MachineService(IMapper mapper, IMachineDataProvider machineDataProvider, IPollingTrustDistributor trustDistributor)
    {
        _mapper = mapper;
        _machineDataProvider = machineDataProvider;
        _trustDistributor = trustDistributor;
    }

    public async Task<GetMachinesResponse> GetMachinesAsync(GetMachinesRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await _machineDataProvider.GetMachinePagingAsync(
            request.SpaceId, request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        return new GetMachinesResponse
        {
            Data = new GetMachinesResponseData
            {
                Count = count,
                Machines = _mapper.Map<List<MachineDto>>(data)
            }
        };
    }

    public async Task<UpdateMachineResponse> UpdateMachineAsync(UpdateMachineCommand command, CancellationToken cancellationToken)
    {
        var machine = await _machineDataProvider.GetMachinesByIdAsync(command.MachineId, cancellationToken).ConfigureAwait(false);

        if (machine == null) throw new MachineNotFoundException(command.MachineId);

        await EnsureNameAvailableIfChangedAsync(machine, command, cancellationToken).ConfigureAwait(false);

        // Validate BEFORE mutating. Round-6: reject cross-style field
        // contamination (e.g. ClusterUrl sent to a TentaclePolling machine)
        // before any JSON deserialise/re-serialise that could corrupt the
        // endpoint. The old `else → K8s` fallthrough silently destroyed
        // Tentacle / Ssh / K8sAgent endpoints.
        EnsureCommandFieldsBelongToMachineStyle(machine, command);

        ApplyCommonFields(machine, command);

        ApplyEndpointUpdate(machine, command);

        await _machineDataProvider.UpdateMachineAsync(machine, cancellationToken: cancellationToken).ConfigureAwait(false);

        _trustDistributor.Reconfigure();

        Log.Information("Updated machine {MachineName} (Id={MachineId})", machine.Name, machine.Id);

        return new UpdateMachineResponse { Data = _mapper.Map<MachineDto>(machine) };
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
    /// belong to the target machine's CommunicationStyle. The old code
    /// fell through to "treat everything non-OpenClaw as KubernetesApi",
    /// silently corrupting Tentacle / Ssh / K8sAgent endpoint JSON on
    /// ambient fields like <c>ResourceReferences</c>. This guard throws
    /// BEFORE any mutation, so no cleanup / transaction is needed.
    /// </summary>
    private static void EnsureCommandFieldsBelongToMachineStyle(Persistence.Entities.Deployments.Machine machine, UpdateMachineCommand c)
    {
        var style = CommunicationStyleParser.Parse(machine.Endpoint);

        void Check(bool isSet, string fieldName, params CommunicationStyle[] allowed)
        {
            if (!isSet || allowed.Contains(style)) return;

            throw new MachineEndpointUpdateNotApplicableException(
                machineId: machine.Id,
                machineStyle: style.ToString(),
                offendingField: fieldName,
                acceptedForStyles: string.Join(", ", allowed.Select(s => s.ToString())));
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
    }

    /// <summary>
    /// Per-style endpoint JSON merge. One method per DTO shape because the
    /// type parameter varies; the body is a homogeneous
    /// <c>endpoint.X = command.X ?? endpoint.X</c> ladder — any field the
    /// command doesn't set is preserved from the existing endpoint JSON.
    /// </summary>
    private static void ApplyEndpointUpdate(Persistence.Entities.Deployments.Machine machine, UpdateMachineCommand c)
    {
        var style = CommunicationStyleParser.Parse(machine.Endpoint);

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
