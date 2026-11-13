using System.Text.Json;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.Deployments;
using Squid.Message.Enums.Events;

namespace Squid.Core.Services.OctopusImport.History;

/// <summary>One resolved Octopus interruption, as a deployment_interruption row.</summary>
public sealed record OctopusImportInterruptionRow(InterruptionType Type, string Title, string Resolution, DateTimeOffset CreatedAt, DateTimeOffset ResolvedAt, string ResponsibleTeamIds, string SubmittedValuesJson);

/// <summary>One audit event of the deployment. <see cref="OctopusCategory"/> and <see cref="Message"/> are null for a synthesized event.</summary>
public sealed record OctopusImportEventRow(EventCategory Category, DateTimeOffset Occurred, string Username, string OctopusCategory, string Message);

/// <summary>
/// Pure mapping of a task's interruptions and events. Only resolved manual interventions and guided failures
/// become rows: an imported task is never resumed, so a pending interruption must never be actionable and is
/// kept in the task json only. Octopus events without a Squid category keep their own category and message.
/// </summary>
public static class OctopusImportHistoryChildMapper
{
    private static readonly Dictionary<string, EventCategory> EventCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DeploymentQueued"] = EventCategory.DeploymentQueued,
        ["DeploymentStarted"] = EventCategory.DeploymentStarted,
        ["DeploymentSucceeded"] = EventCategory.DeploymentSucceeded,
        ["DeploymentFailed"] = EventCategory.DeploymentFailed,
        ["DeploymentCanceled"] = EventCategory.DeploymentCanceled,
        ["DeploymentCancelled"] = EventCategory.DeploymentCanceled,
        ["DeploymentTimedOut"] = EventCategory.DeploymentTimedOut,
        ["ManualInterventionRaised"] = EventCategory.ManualInterventionRaised,
        ["ManualInterventionInterruptionRaised"] = EventCategory.ManualInterventionRaised,
        ["ManualInterventionSubmitted"] = EventCategory.ManualInterventionSubmitted,
        ["GuidedFailureRaised"] = EventCategory.GuidedFailureRaised,
        ["GuidedFailureInterruptionRaised"] = EventCategory.GuidedFailureRaised,
        ["DeploymentResumed"] = EventCategory.DeploymentResumed
    };

    private static readonly Dictionary<string, InterruptionType> InterruptionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ManualIntervention"] = InterruptionType.ManualIntervention,
        ["GuidedFailure"] = InterruptionType.GuidedFailure
    };

    public static EventCategory MapEventCategory(string octopusCategory)
        => EventCategories.TryGetValue(octopusCategory?.Trim() ?? string.Empty, out var category) ? category : EventCategory.ImportedOctopusEvent;

    /// <summary>The terminal event a normal deployment records; null while the task is not finished.</summary>
    public static EventCategory? TerminalEventCategory(OctopusImportTaskState state)
        => state.State switch
        {
            TaskState.Success => EventCategory.DeploymentSucceeded,
            TaskState.Failed => EventCategory.DeploymentFailed,
            TaskState.Cancelled => EventCategory.DeploymentCanceled,
            TaskState.TimedOut => EventCategory.DeploymentTimedOut,
            _ => null
        };

    /// <summary>Octopus's own events, or one synthesized terminal event when Octopus returned none.</summary>
    public static IReadOnlyList<OctopusImportEventRow> MapEvents(IReadOnlyList<OctopusEventDto> events, OctopusImportTaskState state, DateTimeOffset completedAt, string username)
    {
        if (events is { Count: > 0 }) return events.OrderBy(e => e.Occurred).ThenBy(e => e.Id, StringComparer.Ordinal).Select(e => MapEvent(e, completedAt, username)).ToList();

        var terminal = TerminalEventCategory(state);

        return terminal == null ? [] : [new OctopusImportEventRow(terminal.Value, completedAt, username, null, null)];
    }

    public static bool IsResolved(OctopusInterruptionDto interruption)
        => interruption != null && !interruption.IsPending && InterruptionTypes.ContainsKey(interruption.Type?.Trim() ?? string.Empty);

    public static IReadOnlyList<OctopusImportInterruptionRow> MapResolvedInterruptions(OctopusTaskHistoryDetails details, Func<string, int?> teamResolver)
        => (details?.Interruptions ?? []).Where(IsResolved).Select(i => MapInterruption(i, details.Events ?? [], teamResolver)).ToList();

    /// <summary>Everything that does not become a row (pending interruptions, other types): kept in the task json only.</summary>
    public static IReadOnlyList<object> PendingInterruptions(OctopusTaskHistoryDetails details)
        => (details?.Interruptions ?? []).Where(i => !IsResolved(i)).Select(i => (object)new { i.Id, i.Title, i.Type, i.IsPending, i.Created }).ToList();

    public static IReadOnlyList<object> Artifacts(OctopusTaskHistoryDetails details)
        => (details?.Artifacts ?? []).Select(a => (object)new { a.Filename, a.Source, a.Created }).ToList();

    // The category and message are serialized into jsonb, where even an escaped U+0000 is rejected, so they drop it here.
    private static OctopusImportEventRow MapEvent(OctopusEventDto @event, DateTimeOffset completedAt, string username)
        => new(MapEventCategory(@event.Category), @event.Occurred ?? completedAt, string.IsNullOrWhiteSpace(@event.Username) ? username : @event.Username.Trim(), OctopusImportText.StripNul(@event.Category), OctopusImportText.StripNul(@event.Message));

    private static OctopusImportInterruptionRow MapInterruption(OctopusInterruptionDto interruption, IReadOnlyList<OctopusEventDto> events, Func<string, int?> teamResolver)
    {
        var created = interruption.Created ?? DateTimeOffset.UnixEpoch;
        var values = interruption.Form?.Values ?? [];

        return new OctopusImportInterruptionRow(InterruptionTypes[interruption.Type.Trim()], interruption.Title, Resolution(values), created, ResolvedAt(interruption, events) ?? created, TeamIds(interruption.ResponsibleTeamIds, teamResolver), values.Count == 0 ? null : JsonSerializer.Serialize(values));
    }

    private static string Resolution(IReadOnlyDictionary<string, string> values)
        => FirstValue(values, "Result") ?? FirstValue(values, "Guidance") ?? "Resolved";

    private static string FirstValue(IReadOnlyDictionary<string, string> values, string key)
        => values.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value)).Value;

    // The submission event of the interruption: a ManualInterventionSubmitted or GuidedFailure event that refers to it.
    private static DateTimeOffset? ResolvedAt(OctopusInterruptionDto interruption, IReadOnlyList<OctopusEventDto> events)
        => events.Where(e => IsSubmission(e) && (e.RelatedDocumentIds ?? []).Contains(interruption.Id, StringComparer.OrdinalIgnoreCase)).Select(e => e.Occurred).Where(o => o != null).Max();

    private static bool IsSubmission(OctopusEventDto @event)
    {
        var category = @event.Category ?? string.Empty;

        return category.Equals("ManualInterventionSubmitted", StringComparison.OrdinalIgnoreCase) || (category.StartsWith("GuidedFailure", StringComparison.OrdinalIgnoreCase) && !category.EndsWith("Raised", StringComparison.OrdinalIgnoreCase));
    }

    // Unmapped teams are dropped: an imported interruption is resolved and read-only, so nobody is ever asked to act on it.
    private static string TeamIds(IEnumerable<string> octopusTeamIds, Func<string, int?> teamResolver)
    {
        var ids = (octopusTeamIds ?? []).Select(id => teamResolver?.Invoke(id)).Where(id => id is > 0).Select(id => id.Value).Distinct().ToList();

        return ids.Count == 0 ? null : string.Join(",", ids);
    }
}
