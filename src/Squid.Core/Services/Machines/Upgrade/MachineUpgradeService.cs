using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Machines.Exceptions;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Services.Machines.Upgrade;

public sealed class MachineUpgradeService : IMachineUpgradeService
{
    private readonly IMachineDataProvider _machineDataProvider;
    private readonly IMachineRuntimeCapabilitiesCache _runtimeCache;
    private readonly IBundledTentacleVersionProvider _versionProvider;
    private readonly IEnumerable<IMachineUpgradeStrategy> _strategies;

    public MachineUpgradeService(
        IMachineDataProvider machineDataProvider,
        IMachineRuntimeCapabilitiesCache runtimeCache,
        IBundledTentacleVersionProvider versionProvider,
        IEnumerable<IMachineUpgradeStrategy> strategies)
    {
        _machineDataProvider = machineDataProvider;
        _runtimeCache = runtimeCache;
        _versionProvider = versionProvider;
        _strategies = strategies;
    }

    public async Task<UpgradeMachineResponseData> UpgradeAsync(UpgradeMachineCommand command, CancellationToken ct)
    {
        var machine = await _machineDataProvider.GetMachinesByIdAsync(command.MachineId, ct).ConfigureAwait(false)
            ?? throw new MachineNotFoundException(command.MachineId);

        var targetVersion = ResolveTargetVersion(command);
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            return BuildResponse(machine, currentVersion: null, targetVersion: null,
                MachineUpgradeStatus.Failed,
                "No target version provided and no bundled version is configured on this server. " +
                "Specify TargetVersion explicitly in the upgrade request.");
        }

        var currentVersion = ResolveCurrentVersion(machine.Id);

        if (IsAlreadyUpToDate(currentVersion, targetVersion))
        {
            return BuildResponse(machine, currentVersion, targetVersion,
                MachineUpgradeStatus.AlreadyUpToDate,
                $"Machine '{machine.Name}' already on version {currentVersion}; nothing to do.");
        }

        var style = EndpointJsonHelper.GetField(machine.Endpoint, "CommunicationStyle");
        var strategy = _strategies.FirstOrDefault(s => s.CanHandle(style));

        if (strategy == null)
        {
            return BuildResponse(machine, currentVersion, targetVersion,
                MachineUpgradeStatus.NotSupported,
                $"No upgrade strategy registered for CommunicationStyle '{style}'.");
        }

        var outcome = await strategy.UpgradeAsync(machine, targetVersion, ct).ConfigureAwait(false);

        return BuildResponse(machine, currentVersion, targetVersion, outcome.Status, outcome.Detail);
    }

    /// <summary>Operator-supplied version wins over the server bundle when both present.</summary>
    private string ResolveTargetVersion(UpgradeMachineCommand command)
        => !string.IsNullOrWhiteSpace(command.TargetVersion)
            ? command.TargetVersion.Trim()
            : _versionProvider.GetBundledVersion();

    /// <summary>
    /// Reads the agent's last-reported version from the runtime capabilities
    /// cache (populated by <c>TentacleHealthCheckStrategy</c> via Halibut's
    /// Capabilities probe). Empty when the cache is cold.
    /// </summary>
    private string ResolveCurrentVersion(int machineId)
        => _runtimeCache.TryGet(machineId)?.AgentVersion ?? string.Empty;

    /// <summary>
    /// Strict semver compare (no pre-release / metadata handling for now —
    /// falls back to string equality if either side isn't a clean
    /// <c>System.Version</c>). If we can't decide, we treat it as "needs
    /// upgrade" so the operator's request still gets dispatched.
    /// </summary>
    private static bool IsAlreadyUpToDate(string current, string target)
    {
        if (string.IsNullOrWhiteSpace(current)) return false;

        if (Version.TryParse(current, out var c) && Version.TryParse(target, out var t))
            return c >= t;

        return string.Equals(current.Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static UpgradeMachineResponseData BuildResponse(
        Persistence.Entities.Deployments.Machine machine,
        string currentVersion,
        string targetVersion,
        MachineUpgradeStatus status,
        string detail) => new()
    {
        MachineId = machine.Id,
        MachineName = machine.Name,
        CurrentVersion = currentVersion ?? string.Empty,
        TargetVersion = targetVersion ?? string.Empty,
        Status = status,
        Detail = detail
    };
}
