using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums;

namespace Squid.Core.Services.ProjectTriggers.Actions;

public sealed record TriggerActionMachine(string EnvironmentIds, MachineHealthStatus HealthStatus);

public sealed record TriggerPackageAction(int Id, string Name, string ActionType);

public interface ITriggerActionDataProvider : IScopedDependency
{
    bool HasActiveTransaction { get; }

    Task<Project> GetEnabledProjectAsync(int spaceId, int projectId, CancellationToken ct);

    Task<bool> ReleaseExistsAsync(int spaceId, int projectId, int releaseId, int? channelId, CancellationToken ct);

    Task<int> CountEnvironmentsAsync(int spaceId, int[] environmentIds, CancellationToken ct);

    Task<List<TriggerActionMachine>> GetEnabledMachinesAsync(int spaceId, int[] machineIds, CancellationToken ct);

    Task<int?> GetReleaseProcessIdAsync(int spaceId, int projectId, int? channelId, CancellationToken ct);

    Task<int> CountFeedsAsync(int spaceId, int[] feedIds, CancellationToken ct);

    Task<List<TriggerPackageAction>> GetApplicablePackageActionsAsync(
        int processId, int channelId, CancellationToken ct);

    Task<List<DeploymentActionProperty>> GetActionPropertiesAsync(int[] actionIds, CancellationToken ct);

    Task LockReleaseVersionAsync(int channelId, CancellationToken ct);

    Task<List<string>> GetReleaseVersionsAsync(int spaceId, int projectId, int channelId, CancellationToken ct);

    IAsyncEnumerable<SuccessfulTriggerDeployment> ReadSuccessfulDeploymentsAsync(
        int spaceId, int projectId, int environmentId, int? channelId, int? deploymentId);
}

public sealed partial class TriggerActionDataProvider : ITriggerActionDataProvider
{
    private const int ReleaseVersionLockNamespace = 0x53515456; // SQTV; channel IDs are globally unique.
    private readonly IRepository _repository;

    public TriggerActionDataProvider(IRepository repository)
    {
        _repository = repository;
    }

    public bool HasActiveTransaction
    {
        get
        {
            return _repository.Database.CurrentTransaction != null;
        }
    }

    public async Task<Project> GetEnabledProjectAsync(int spaceId, int projectId, CancellationToken ct)
    {
        return await _repository.QueryNoTracking<Project>(
                p => p.Id == projectId && p.SpaceId == spaceId && !p.IsDisabled)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> ReleaseExistsAsync(
        int spaceId, int projectId, int releaseId, int? channelId, CancellationToken ct)
    {
        var candidates = from release in _repository.QueryNoTracking<Release>(
                             r => r.Id == releaseId && r.ProjectId == projectId && r.SpaceId == spaceId)
                         join channel in _repository.QueryNoTracking<Channel>(
                             c => c.ProjectId == projectId && c.SpaceId == spaceId)
                             on release.ChannelId equals channel.Id
                         where channelId == null || channel.Id == channelId
                         select release.Id;

        return await candidates.AnyAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountEnvironmentsAsync(int spaceId, int[] environmentIds, CancellationToken ct)
    {
        return await _repository.CountAsync<Persistence.Entities.Deployments.Environment>(
            e => environmentIds.Contains(e.Id) && e.SpaceId == spaceId, ct).ConfigureAwait(false);
    }

    public async Task<List<TriggerActionMachine>> GetEnabledMachinesAsync(
        int spaceId, int[] machineIds, CancellationToken ct)
    {
        return await _repository.QueryNoTracking<Machine>(
                m => machineIds.Contains(m.Id) && m.SpaceId == spaceId && !m.IsDisabled)
            .Select(m => new TriggerActionMachine(m.EnvironmentIds, m.HealthStatus))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<int?> GetReleaseProcessIdAsync(
        int spaceId, int projectId, int? channelId, CancellationToken ct)
    {
        var scope = from project in _repository.QueryNoTracking<Project>(
                        p => p.Id == projectId && p.SpaceId == spaceId && !p.IsDisabled)
                    join channel in _repository.QueryNoTracking<Channel>(
                        c => c.SpaceId == spaceId && c.Id == channelId) on project.Id equals channel.ProjectId
                    join process in _repository.QueryNoTracking<DeploymentProcess>(
                        p => p.SpaceId == spaceId && p.ProjectId == projectId)
                        on project.DeploymentProcessId equals process.Id
                    select (int?)process.Id;

        return await scope.SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountFeedsAsync(int spaceId, int[] feedIds, CancellationToken ct)
    {
        return await _repository.CountAsync<ExternalFeed>(
            f => f.SpaceId == spaceId && feedIds.Contains(f.Id), ct).ConfigureAwait(false);
    }

    public async Task<List<TriggerPackageAction>> GetApplicablePackageActionsAsync(
        int processId, int channelId, CancellationToken ct)
    {
        var channels = _repository.QueryNoTracking<ActionChannel>();
        var query = from step in _repository.QueryNoTracking<DeploymentStep>(
                        s => s.ProcessId == processId && !s.IsDisabled)
                    join action in _repository.QueryNoTracking<DeploymentAction>(a => !a.IsDisabled)
                        on step.Id equals action.StepId
                    where !channels.Any(c => c.ActionId == action.Id)
                          || channels.Any(c => c.ActionId == action.Id && c.ChannelId == channelId)
                    orderby step.StepOrder, step.Id, action.ActionOrder, action.Id
                    select new TriggerPackageAction(action.Id, action.Name, action.ActionType);

        return await query.ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<DeploymentActionProperty>> GetActionPropertiesAsync(int[] actionIds, CancellationToken ct)
    {
        return await _repository.QueryNoTracking<DeploymentActionProperty>(p => actionIds.Contains(p.ActionId))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task LockReleaseVersionAsync(int channelId, CancellationToken ct)
    {
        await _repository.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({ReleaseVersionLockNamespace}, {channelId})", ct).ConfigureAwait(false);
    }

    public async Task<List<string>> GetReleaseVersionsAsync(
        int spaceId, int projectId, int channelId, CancellationToken ct)
    {
        return await _repository.QueryNoTracking<Release>(
                r => r.ProjectId == projectId && r.SpaceId == spaceId && r.ChannelId == channelId)
            .Select(r => r.Version).ToListAsync(ct).ConfigureAwait(false);
    }
}
