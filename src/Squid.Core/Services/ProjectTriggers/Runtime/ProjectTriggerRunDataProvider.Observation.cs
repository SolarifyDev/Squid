using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.Core.Services.ProjectTriggers.Runtime;

public sealed partial class ProjectTriggerRunDataProvider
{
    public async Task<ProjectTriggerSourceSnapshot> ReadSnapshotAsync(
        int triggerId, string sourceKey, CancellationToken ct)
    {
        var triggers = _repository.QueryNoTracking<ProjectTrigger>();
        var states = _repository.QueryNoTracking<ProjectTriggerState>();

        return await (from trigger in triggers
                      where trigger.Id == triggerId && !trigger.IsDisabled && !trigger.IsDeleted
                      from state in states
                          .Where(s => s.TriggerId == trigger.Id && s.SourceKey == sourceKey).DefaultIfEmpty()
                      select new ProjectTriggerSourceSnapshot(trigger, state))
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<ProjectTriggerObservationCommit> CommitObservationAsync(
        ProjectTriggerSourceSnapshot snapshot,
        string sourceKey,
        TriggerObservation observation,
        TriggerEvaluation evaluation,
        TimeProvider time,
        CancellationToken ct)
    {
        await using var transaction = await _repository.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var trigger = await LockObservationTriggerAsync(snapshot.Trigger.Id, ct).ConfigureAwait(false);

        if (trigger == null || trigger.IsDisabled || trigger.IsDeleted ||
            !ProjectTriggerRunPolicy.SameExecution(snapshot.Trigger, trigger)) return new(false, null);

        var state = await _repository.SingleOrDefaultAsync<ProjectTriggerState>(
            s => s.TriggerId == trigger.Id && s.SourceKey == sourceKey, ct).ConfigureAwait(false);

        if (!SameCursor(snapshot.State, state)) return new(true, null);

        var now = time.GetUtcNow();
        var added = await AppendRunsAsync(trigger, observation, evaluation.Occurrences, now, ct).ConfigureAwait(false);

        await SaveSourceStateAsync(trigger.Id, sourceKey, state, evaluation, now, ct).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new(false, added);
    }

    private async Task<ProjectTrigger> LockObservationTriggerAsync(int triggerId, CancellationToken ct)
    {
        // Fence observations, edits, disable/delete and management cursor resets with the same short row lock.
        return await _db.Set<ProjectTrigger>()
            .FromSqlInterpolated($"SELECT * FROM project_trigger WHERE id = {triggerId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    private static bool SameCursor(ProjectTriggerState expected, ProjectTriggerState current)
    {
        return current?.Id == expected?.Id && current?.Cursor == expected?.Cursor &&
            current?.NextDueAt == expected?.NextDueAt;
    }

    private async Task SaveSourceStateAsync(
        int triggerId,
        string sourceKey,
        ProjectTriggerState state,
        TriggerEvaluation evaluation,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (state == null)
        {
            state = new ProjectTriggerState { TriggerId = triggerId, SourceKey = sourceKey };

            await _repository.InsertAsync(state, ct).ConfigureAwait(false);
        }

        state.Cursor = evaluation.NextCursor;
        state.LastCheckedAt = now;
        state.LastOutcome = evaluation.Summary;
        state.NextDueAt = evaluation.NextDueAt?.ToUniversalTime();
    }

    private async Task<int> AppendRunsAsync(
        ProjectTrigger trigger,
        TriggerObservation observation,
        IReadOnlyList<TriggerOccurrence> occurrences,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (occurrences.Count == 0) return 0;

        var keys = occurrences.Select(o => o.OccurrenceKey).Distinct(StringComparer.Ordinal).ToArray();
        var existing = await _repository.Query<ProjectTriggerRun>()
            .Where(r => r.TriggerId == trigger.Id && keys.Contains(r.OccurrenceKey))
            .Select(r => r.OccurrenceKey).ToListAsync(ct).ConfigureAwait(false);
        var seen = existing.ToHashSet(StringComparer.Ordinal);
        var added = 0;

        foreach (var occurrence in occurrences)
        {
            if (!seen.Add(occurrence.OccurrenceKey)) continue;

            var run = CreateRun(trigger, observation, occurrence, now);

            await _repository.InsertAsync(run, ct).ConfigureAwait(false);
            added++;
        }

        return added;
    }

    private static ProjectTriggerRun CreateRun(
        ProjectTrigger trigger, TriggerObservation observation, TriggerOccurrence occurrence, DateTimeOffset now)
    {
        return new ProjectTriggerRun
        {
            TriggerId = trigger.Id,
            Revision = trigger.Revision,
            ExecutionUserId = trigger.ExecutionUserId,
            TriggerType = trigger.TriggerType,
            ActionType = trigger.ActionType,
            ConfigurationJson = trigger.ConfigurationJson,
            SourceKey = observation.SourceKey,
            OccurrenceKey = occurrence.OccurrenceKey,
            ObservedAt = observation.ObservedAt.ToUniversalTime(),
            EvidenceJson = occurrence.EvidenceJson ?? observation.EvidenceJson,
            ActionPlanJson = occurrence.Action == null ? null : JsonSerializer.Serialize(occurrence.Action),
            Status = occurrence.Action == null ? ProjectTriggerRunStatuses.Skipped : ProjectTriggerRunStatuses.Pending,
            Reason = occurrence.SkipReason,
            CompletedAt = occurrence.Action == null ? now : null,
            NextAttemptAt = now
        };
    }

    public async Task<List<ProjectTriggerDueSource>> FindDueSourcesAsync(
        string triggerType, string sourceKey, DateTimeOffset now, int limit, CancellationToken ct)
    {
        var triggers = _repository.Query<ProjectTrigger>();
        var states = _repository.Query<ProjectTriggerState>();

        return await (from trigger in triggers
                      where trigger.TriggerType == triggerType && !trigger.IsDisabled && !trigger.IsDeleted
                      from state in states
                          .Where(s => s.TriggerId == trigger.Id && s.SourceKey == sourceKey).DefaultIfEmpty()
                      where state == null || state.NextDueAt <= now
                      orderby state.NextDueAt ?? DateTimeOffset.MinValue, trigger.Id
                      select new ProjectTriggerDueSource(trigger.Id, state.NextDueAt))
            .Take(limit).ToListAsync(ct).ConfigureAwait(false);
    }
}
