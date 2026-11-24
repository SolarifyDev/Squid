using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.Core.Services.ProjectTriggers.Management;

public interface IProjectTriggerDataProvider : IScopedDependency
{
    Task AddAsync(ProjectTrigger trigger, CancellationToken ct);

    Task<ProjectTrigger> GetForEditAsync(Project project, int triggerId, CancellationToken ct);

    Task SaveRevisionAsync(ProjectTrigger trigger, bool resetState, CancellationToken ct);

    Task<ProjectTriggerPage<ProjectTriggerSummaryDto>> ListAsync(
        Project project, int skip, int take, CancellationToken ct);

    Task<(ProjectTriggerDto Model, string ConfigurationJson)> DetailAsync(
        Project project, int triggerId, CancellationToken ct);

    Task<ProjectTriggerPage<ProjectTriggerRunDto>> HistoryAsync(
        Project project, int triggerId, int skip, int take, CancellationToken ct);

    Task<bool> IsExecutionUserActiveAsync(int userId, CancellationToken ct);

    Task<Project> GetProjectAsync(int projectId, CancellationToken ct);

    Task<bool> HasChannelAsync(int channelId, int projectId, int spaceId, CancellationToken ct);

    Task<int> CountEnvironmentsAsync(int[] environmentIds, int spaceId, CancellationToken ct);
}

public sealed class ProjectTriggerDataProvider : IProjectTriggerDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ProjectTriggerDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddAsync(ProjectTrigger trigger, CancellationToken ct)
    {
        await _repository.InsertAsync(trigger, ct).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<ProjectTrigger> GetForEditAsync(Project project, int triggerId, CancellationToken ct)
    {
        return await _repository.Query<ProjectTrigger>().SingleOrDefaultAsync(t =>
            t.Id == triggerId && t.ProjectId == project.Id && t.SpaceId == project.SpaceId && !t.IsDeleted, ct)
            .ConfigureAwait(false) ?? throw new ProjectTriggerNotFoundException();
    }

    public async Task SaveRevisionAsync(ProjectTrigger trigger, bool resetState, CancellationToken ct)
    {
        // Take the concurrency-checked row lock before removing cursors. Runs keep their occurrence keys.
        try
        {
            await using var transaction = resetState
                ? await _repository.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
                : null;

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

            if (resetState)
            {
                await _repository.ExecuteDeleteAsync<ProjectTriggerState>(s => s.TriggerId == trigger.Id, ct)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            _repository.Detach(trigger);

            throw new ProjectTriggerRevisionConflictException();
        }
        catch
        {
            _repository.Detach(trigger);

            throw;
        }
    }

    public async Task<ProjectTriggerPage<ProjectTriggerSummaryDto>> ListAsync(
        Project project, int skip, int take, CancellationToken ct)
    {
        var triggers = VisibleTriggers(project);
        var count = await triggers.CountAsync(ct).ConfigureAwait(false);
        var page = triggers.OrderBy(t => t.Id).Skip(skip).Take(take);
        var rows = await ReadRows(page, includeConfiguration: false).ToListAsync(ct).ConfigureAwait(false);

        return new ProjectTriggerPage<ProjectTriggerSummaryDto>(
            count, rows.OrderBy(r => r.Model.Id).Select(r => (ProjectTriggerSummaryDto)r.Model).ToList());
    }

    public async Task<(ProjectTriggerDto Model, string ConfigurationJson)> DetailAsync(
        Project project, int triggerId, CancellationToken ct)
    {
        var trigger = VisibleTriggers(project).Where(t => t.Id == triggerId);
        var row = await ReadRows(trigger, includeConfiguration: true).SingleOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new ProjectTriggerNotFoundException();

        return (row.Model, row.ConfigurationJson);
    }

    public async Task<ProjectTriggerPage<ProjectTriggerRunDto>> HistoryAsync(
        Project project, int triggerId, int skip, int take, CancellationToken ct)
    {
        // A tombstoned definition still owns its runs. Its original project and space gate every history read.
        if (!await _repository.AnyAsync<ProjectTrigger>(t =>
                t.Id == triggerId && t.ProjectId == project.Id && t.SpaceId == project.SpaceId, ct).ConfigureAwait(false))
            throw new ProjectTriggerNotFoundException();

        var runs = _repository.QueryNoTracking<ProjectTriggerRun>().Where(r => r.TriggerId == triggerId);
        var count = await runs.CountAsync(ct).ConfigureAwait(false);
        var entries = await runs.OrderByDescending(r => r.ObservedAt).ThenByDescending(r => r.Id).Skip(skip).Take(take)
            .Select(r => new ProjectTriggerRunDto
            {
                Id = r.Id,
                TriggerId = r.TriggerId,
                Revision = r.Revision,
                ExecutionUserId = r.ExecutionUserId,
                TriggerType = r.TriggerType,
                ActionType = r.ActionType,
                ObservedAt = r.ObservedAt,
                CompletedAt = r.CompletedAt,
                Status = r.Status,
                Reason = r.Reason,
                Attempts = r.Attempts,
                ReleaseId = r.ReleaseId,
                DeploymentId = r.DeploymentId,
                TaskId = r.TaskId
            }).ToListAsync(ct).ConfigureAwait(false);

        return new ProjectTriggerPage<ProjectTriggerRunDto>(count, entries);
    }

    public async Task<bool> IsExecutionUserActiveAsync(int userId, CancellationToken ct)
    {
        return await _repository.AnyAsync<UserAccount>(u => u.Id == userId && !u.IsDisabled, ct)
            .ConfigureAwait(false);
    }

    public async Task<Project> GetProjectAsync(int projectId, CancellationToken ct)
    {
        return await _repository.QueryNoTracking<Project>().SingleOrDefaultAsync(p => p.Id == projectId, ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> HasChannelAsync(int channelId, int projectId, int spaceId, CancellationToken ct)
    {
        return await _repository.AnyAsync<Channel>(c =>
            c.Id == channelId && c.ProjectId == projectId && c.SpaceId == spaceId, ct).ConfigureAwait(false);
    }

    public async Task<int> CountEnvironmentsAsync(int[] environmentIds, int spaceId, CancellationToken ct)
    {
        return await _repository.CountAsync<Persistence.Entities.Deployments.Environment>(e =>
            environmentIds.Contains(e.Id) && e.SpaceId == spaceId, ct).ConfigureAwait(false);
    }

    private IQueryable<ProjectTrigger> VisibleTriggers(Project project)
    {
        return _repository.QueryNoTracking<ProjectTrigger>()
            .Where(t => t.ProjectId == project.Id && t.SpaceId == project.SpaceId && !t.IsDeleted);
    }

    private IQueryable<ReadRow> ReadRows(IQueryable<ProjectTrigger> triggers, bool includeConfiguration)
    {
        var states = _repository.QueryNoTracking<ProjectTriggerState>();

        return from t in triggers
               from last in states.Where(s => s.TriggerId == t.Id && s.LastCheckedAt.HasValue)
                   .OrderByDescending(s => s.LastCheckedAt).ThenByDescending(s => s.Id).Take(1).DefaultIfEmpty()
               select new ReadRow(new ProjectTriggerDto
               {
                   Id = t.Id,
                   SpaceId = t.SpaceId,
                   ProjectId = t.ProjectId,
                   Name = t.Name,
                   Description = t.Description,
                   TriggerType = t.TriggerType,
                   ActionType = t.ActionType,
                   IsDisabled = t.IsDisabled,
                   Revision = t.Revision,
                   ExecutionUserId = t.ExecutionUserId,
                   CreatedDate = t.CreatedDate,
                   CreatedBy = t.CreatedBy,
                   LastModifiedDate = t.LastModifiedDate,
                   LastModifiedBy = t.LastModifiedBy,
                   LastCheckedAt = last.LastCheckedAt,
                   LastOutcome = last.LastOutcome,
                   NextDueAt = t.IsDisabled ? null : states.Where(s => s.TriggerId == t.Id).Min(s => s.NextDueAt)
               }, includeConfiguration ? t.ConfigurationJson : null);
    }

    private sealed record ReadRow(ProjectTriggerDto Model, string ConfigurationJson);
}
