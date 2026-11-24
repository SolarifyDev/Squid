namespace Squid.Core.Services.ProjectTriggers.Runtime;

/// <summary>A trigger type may register polling sources with distinct stable keys. Return the next due time in the handler's evaluation.</summary>
public interface IProjectTriggerPollingSource : IScopedDependency
{
    string TriggerType { get; }
    string SourceKey { get; }
    Task<TriggerObservation> PollAsync(ProjectTriggerDefinition definition, string cursor, DateTimeOffset now, CancellationToken ct);
}

public interface IProjectTriggerDueScanner : IScopedDependency
{
    Task<int> ScanDueAsync(int batchSize, CancellationToken ct = default);
}

public sealed class ProjectTriggerDueScanner : IProjectTriggerDueScanner
{
    private readonly ILifetimeScope _lifetimeScope;
    private readonly TimeProvider _time;

    public ProjectTriggerDueScanner(ILifetimeScope lifetimeScope, TimeProvider time = null)
    {
        _lifetimeScope = lifetimeScope;
        _time = time ?? TimeProvider.System;
    }

    public async Task<int> ScanDueAsync(int batchSize, CancellationToken ct = default)
    {
        var limit = ProjectTriggerRunPolicy.BatchLimit(batchSize);

        if (limit == 0) return 0;

        await using var scope = _lifetimeScope.BeginLifetimeScope();
        var dataProvider = scope.Resolve<IProjectTriggerRunDataProvider>();
        var now = _time.GetUtcNow();
        var due = await FindDueAsync(scope, dataProvider, now, limit, ct).ConfigureAwait(false);
        var observer = scope.Resolve<ProjectTriggerObserver>();

        return await PollDueAsync(observer, due, limit, ct).ConfigureAwait(false);
    }

    private static async Task<List<(IProjectTriggerPollingSource Source, ProjectTriggerDueSource Trigger)>> FindDueAsync(
        ILifetimeScope scope, IProjectTriggerRunDataProvider dataProvider, DateTimeOffset now, int limit, CancellationToken ct)
    {
        var due = new List<(IProjectTriggerPollingSource Source, ProjectTriggerDueSource Trigger)>();

        foreach (var source in scope.Resolve<IEnumerable<IProjectTriggerPollingSource>>())
        {
            ProjectTriggerObserver.ValidateKey(source.SourceKey);

            var candidates = await dataProvider.FindDueSourcesAsync(
                source.TriggerType, source.SourceKey, now, limit, ct).ConfigureAwait(false);

            due.AddRange(candidates.Select(t => (source, t)));
        }

        return due;
    }

    private static async Task<int> PollDueAsync(
        ProjectTriggerObserver observer,
        List<(IProjectTriggerPollingSource Source, ProjectTriggerDueSource Trigger)> due,
        int limit,
        CancellationToken ct)
    {
        var ordered = due.OrderBy(item => item.Trigger.At).ThenBy(item => item.Trigger.Id)
            .ThenBy(item => item.Source.SourceKey, StringComparer.Ordinal).Take(limit);
        var checkedCount = 0;

        foreach (var (source, trigger) in ordered)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (await observer.PollAsync(trigger.Id, source.TriggerType, source.SourceKey, ct).ConfigureAwait(false)) checkedCount++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Warning("Project trigger {TriggerId} check could not be committed ({ErrorType})", trigger.Id, ex.GetType().Name);
            }
        }

        return checkedCount;
    }
}
