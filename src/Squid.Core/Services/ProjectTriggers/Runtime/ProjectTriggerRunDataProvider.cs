using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.ProjectTriggers.Runtime;

public interface IProjectTriggerRunDataProvider : IScopedDependency
{
    Task<List<ProjectTriggerRun>> FindDueRunsAsync(string status, DateTimeOffset now, int limit, CancellationToken ct);

    Task<ProjectTriggerRun> LockRunAsync(long id, string status, DateTimeOffset now, CancellationToken ct);

    Task<ProjectTrigger> GetTriggerAsync(int triggerId, CancellationToken ct);

    Task<ProjectTrigger> LockExecutionTriggerAsync(int triggerId, CancellationToken ct);

    Task<IAsyncDisposable> BeginRunTransactionAsync(CancellationToken ct);

    Task CommitRunAsync(CancellationToken ct);

    Task SaveBusinessActionAsync(CancellationToken ct);

    Task CreateActionSavepointAsync(CancellationToken ct);

    Task RollbackActionAsync(ProjectTriggerRun run, CancellationToken ct);

    Task<bool> TaskExistsAsync(int taskId, CancellationToken ct);

    Task AcknowledgeTaskAsync(int taskId, string jobId, CancellationToken ct);

    Task<ProjectTriggerSourceSnapshot> ReadSnapshotAsync(int triggerId, string sourceKey, CancellationToken ct);

    Task<ProjectTriggerObservationCommit> CommitObservationAsync(
        ProjectTriggerSourceSnapshot snapshot,
        string sourceKey,
        TriggerObservation observation,
        TriggerEvaluation evaluation,
        TimeProvider time,
        CancellationToken ct);

    Task<List<ProjectTriggerDueSource>> FindDueSourcesAsync(
        string triggerType,
        string sourceKey,
        DateTimeOffset now,
        int limit,
        CancellationToken ct);
}

public sealed record ProjectTriggerSourceSnapshot(ProjectTrigger Trigger, ProjectTriggerState State);

public sealed record ProjectTriggerObservationCommit(bool CursorChanged, int? Added);

public sealed record ProjectTriggerDueSource(int Id, DateTimeOffset? At);

public sealed partial class ProjectTriggerRunDataProvider : IProjectTriggerRunDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly SquidDbContext _db;

    public ProjectTriggerRunDataProvider(IRepository repository, IUnitOfWork unitOfWork, SquidDbContext db)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _db = db;
    }

    public async Task<List<ProjectTriggerRun>> FindDueRunsAsync(
        string status, DateTimeOffset now, int limit, CancellationToken ct)
    {
        // Discovery releases these locks before actor-scoped preparation. The run transaction checks again.
        return await _db.Set<ProjectTriggerRun>().FromSqlInterpolated($"""
                SELECT * FROM project_trigger_run WHERE status = {status} AND next_attempt_at <= {now}
                ORDER BY next_attempt_at, id LIMIT {limit} FOR UPDATE SKIP LOCKED
                """).AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<ProjectTriggerRun> LockRunAsync(
        long id, string status, DateTimeOffset now, CancellationToken ct)
    {
        return await _db.Set<ProjectTriggerRun>().FromSqlInterpolated($"""
                SELECT * FROM project_trigger_run WHERE id = {id} AND status = {status} AND next_attempt_at <= {now} FOR UPDATE SKIP LOCKED
                """).SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<ProjectTrigger> GetTriggerAsync(int triggerId, CancellationToken ct)
    {
        return await _repository.QueryNoTracking<ProjectTrigger>()
            .SingleOrDefaultAsync(t => t.Id == triggerId, ct).ConfigureAwait(false);
    }

    public async Task<ProjectTrigger> LockExecutionTriggerAsync(int triggerId, CancellationToken ct)
    {
        // SHARE fences edits until the business commit while independent runs can proceed together.
        return await _db.Set<ProjectTrigger>()
            .FromSqlInterpolated($"SELECT * FROM project_trigger WHERE id = {triggerId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<IAsyncDisposable> BeginRunTransactionAsync(CancellationToken ct)
    {
        return await _repository.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
    }

    public async Task CommitRunAsync(CancellationToken ct)
    {
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        await _repository.Database.CurrentTransaction!.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveBusinessActionAsync(CancellationToken ct)
    {
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task CreateActionSavepointAsync(CancellationToken ct)
    {
        await _repository.Database.CurrentTransaction!
            .CreateSavepointAsync("before_trigger_action", ct).ConfigureAwait(false);
    }

    public async Task RollbackActionAsync(ProjectTriggerRun run, CancellationToken ct)
    {
        await _repository.Database.CurrentTransaction!
            .RollbackToSavepointAsync("before_trigger_action", ct).ConfigureAwait(false);

        _repository.ClearChangeTracker();
        _db.Attach(run);
    }

    public async Task<bool> TaskExistsAsync(int taskId, CancellationToken ct)
    {
        return await _repository.AnyAsync<ServerTask>(t => t.Id == taskId, ct).ConfigureAwait(false);
    }

    public async Task AcknowledgeTaskAsync(int taskId, string jobId, CancellationToken ct)
    {
        // A consumer slot retry or an operator resume may already have replaced this task's job ID.
        await _repository.ExecuteUpdateAsync<ServerTask>(
            t => t.Id == taskId && (t.JobId == null || t.JobId == ""),
            set => set.SetProperty(t => t.JobId, jobId),
            ct).ConfigureAwait(false);
    }
}
