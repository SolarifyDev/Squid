using System.Text;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.ProjectTriggers.Management;

namespace Squid.Core.Services.ProjectTriggers.Runtime;

public interface IProjectTriggerObserver : IScopedDependency
{
    /// <summary>Evaluates one observation against its durable source cursor and returns the number of new runs.</summary>
    Task<int> ObserveAsync(int triggerId, TriggerObservation observation, CancellationToken ct = default);
}

public sealed class ProjectTriggerObserver : IProjectTriggerObserver
{
    private readonly ILifetimeScope _lifetimeScope;
    private readonly TimeProvider _time;

    public ProjectTriggerObserver(ILifetimeScope lifetimeScope, TimeProvider time = null)
    {
        _lifetimeScope = lifetimeScope;
        _time = time ?? TimeProvider.System;
    }

    public async Task<int> ObserveAsync(int triggerId, TriggerObservation observation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateKey(observation.SourceKey);

        return await CheckAsync(triggerId, observation.SourceKey, observation, null, ct).ConfigureAwait(false) ?? 0;
    }

    internal async Task<bool> PollAsync(int triggerId, string triggerType, string sourceKey, CancellationToken ct)
    {
        return (await CheckAsync(triggerId, sourceKey, null, triggerType, ct).ConfigureAwait(false)).HasValue;
    }

    private async Task<int?> CheckAsync(
        int id, string key, TriggerObservation input, string pollingType, CancellationToken ct)
    {
        ProjectTrigger initial = null;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            // Isolate publisher changes and transactions. A losing cursor receives a fresh read and evaluation.
            await using var scope = _lifetimeScope.BeginLifetimeScope();
            var dataProvider = scope.Resolve<IProjectTriggerRunDataProvider>();
            var snapshot = await dataProvider.ReadSnapshotAsync(id, key, ct).ConfigureAwait(false);

            if (snapshot == null ||
                initial != null && !ProjectTriggerRunPolicy.SameExecution(initial, snapshot.Trigger)) return null;
            if (pollingType != null && !IsDue(snapshot, pollingType)) return null;

            initial ??= snapshot.Trigger;

            var check = await EvaluateAsync(scope, dataProvider, snapshot, key, input, pollingType, ct).ConfigureAwait(false);
            var result = await dataProvider.CommitObservationAsync(
                snapshot, key, check.Observation, check.Evaluation, _time, ct).ConfigureAwait(false);

            if (!result.CursorChanged) return result.Added;
        }

        throw new InvalidOperationException("The trigger source cursor changed repeatedly. Retry the observation.");
    }

    private async Task<(TriggerObservation Observation, TriggerEvaluation Evaluation)> EvaluateAsync(
        ILifetimeScope scope,
        IProjectTriggerRunDataProvider dataProvider,
        ProjectTriggerSourceSnapshot snapshot,
        string key,
        TriggerObservation input,
        string pollingType,
        CancellationToken ct)
    {
        var definition = Definition(snapshot.Trigger);
        var cursor = snapshot.State?.Cursor;
        var now = _time.GetUtcNow();

        await using var actor = ProjectTriggerExecutionUser.BeginScope(scope, snapshot.Trigger.ExecutionUserId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (pollingType != null) timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            return await EvaluateSourceAsync(
                actor, snapshot, definition, key, input, pollingType, now, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Warning("Project trigger {TriggerId} source check failed ({ErrorType})", snapshot.Trigger.Id, ex.GetType().Name);

            var failed = input ?? new TriggerObservation(key, "{}", now);
            var reason = ex is PermissionDeniedException ? "Source check authorization was denied." : "Source check failed.";
            var outcome = new TriggerEvaluation(cursor, [], reason,
                pollingType == null ? snapshot.State?.NextDueAt : now.AddMinutes(1));

            if (pollingType != null) return (failed, outcome);

            await dataProvider.CommitObservationAsync(snapshot, key, failed, outcome, _time, ct).ConfigureAwait(false);

            throw;
        }
    }

    private static async Task<(TriggerObservation Observation, TriggerEvaluation Evaluation)> EvaluateSourceAsync(
        ILifetimeScope actor,
        ProjectTriggerSourceSnapshot snapshot,
        ProjectTriggerDefinition definition,
        string key,
        TriggerObservation input,
        string pollingType,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var handler = actor.Resolve<IProjectTriggerRegistry>().Resolve(definition.TriggerType, definition.ActionType);
        var resources = await handler.ValidateAsync(definition, ct).ConfigureAwait(false);

        await actor.Resolve<IProjectTriggerAccess>()
            .EnsureActionAsync(snapshot.Trigger.ExecutionUserId, definition, resources, ct).ConfigureAwait(false);

        var observation = input;

        if (pollingType != null)
            observation = await PollSourceAsync(
                actor, definition, pollingType, key, snapshot.State?.Cursor, now, ct).ConfigureAwait(false);

        if (observation?.SourceKey != key)
            throw new InvalidOperationException("The polling source returned an observation for a different source key.");

        var evaluation = await handler.EvaluateAsync(definition, observation, snapshot.State?.Cursor, ct).ConfigureAwait(false);

        ValidateEvaluation(evaluation, observation);

        return (observation, evaluation);
    }

    private static async Task<TriggerObservation> PollSourceAsync(
        ILifetimeScope actor,
        ProjectTriggerDefinition definition,
        string pollingType,
        string key,
        string cursor,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var source = actor.Resolve<IEnumerable<IProjectTriggerPollingSource>>()
            .Single(s => s.TriggerType == pollingType && s.SourceKey == key);

        return await source.PollAsync(definition, cursor, now, ct).ConfigureAwait(false);
    }

    private bool IsDue(ProjectTriggerSourceSnapshot snapshot, string pollingType)
    {
        return snapshot.Trigger.TriggerType == pollingType &&
            (snapshot.State == null || snapshot.State.NextDueAt <= _time.GetUtcNow());
    }

    private static void ValidateEvaluation(TriggerEvaluation evaluation, TriggerObservation observation)
    {
        ArgumentNullException.ThrowIfNull(evaluation?.Occurrences);

        foreach (var occurrence in evaluation.Occurrences)
        {
            ValidateKey(occurrence?.OccurrenceKey);

            using var evidence = JsonDocument.Parse(occurrence.EvidenceJson ?? observation.EvidenceJson);

            if ((occurrence.Action == null) == string.IsNullOrWhiteSpace(occurrence.SkipReason))
                throw new InvalidOperationException("A trigger occurrence must have exactly one action plan or skip reason.");
        }
    }

    internal static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\0') || key.Length > 512 && key.EnumerateRunes().Count() > 512)
            throw new ArgumentException("Trigger source and occurrence keys must contain between 1 and 512 characters.");
    }

    internal static ProjectTriggerDefinition Definition(ProjectTrigger trigger)
    {
        return new(trigger.ProjectId, trigger.SpaceId, trigger.TriggerType, trigger.ActionType, trigger.ConfigurationJson);
    }
}
