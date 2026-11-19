using System.Globalization;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The field-level old → new of what an Update or a Fill writes into rows an earlier import created. It names exactly the fields the
/// writers rewrite (<see cref="OctopusImportFingerprint"/>): a release's notes, a task's scalars and details, a filled secret (named only).
/// Values are compared the way the fingerprint canonicalises them, so a field shows up only when its stored value really differs.
/// </summary>
public static class OctopusImportPreviewChanges
{
    public const string ReleaseNotesField = "ReleaseNotes";
    public const string StateField = "State";
    public const string QueueTimeField = "QueueTime";
    public const string StartTimeField = "StartTime";
    public const string CompletedTimeField = "CompletedTime";
    public const string ErrorMessageField = "ErrorMessage";
    public const string HasWarningsOrErrorsField = "HasWarningsOrErrors";
    public const string DetailsField = "Details";
    public const string TaskField = "ServerTask";
    public const string RecordField = "History";
    public const string VariableValueField = "Value";
    public const string FeedPasswordField = "Password";
    public const string FeedUsernameField = "Username";
    public const string OwnerField = "Owner";

    /// <summary>Longer texts (release notes, error messages) are cut to this many characters for display.</summary>
    public const int MaxDisplayLength = 500;

    public const string DetailsChangedDescription = "The task log, interruptions, events or artifacts changed in Octopus; they are imported again.";
    public const string TaskRowMissingDescription = "The task an earlier import wrote is no longer in Squid; it is written again.";
    public const string RecordIncompleteDescription = "An earlier import recorded this history incompletely; it is read and written again.";
    public const string RelinkedDescription = "What it belongs to (project, channel, release or environment) was deleted in Squid and is imported again; it is moved onto that, not imported a second time.";
    public const string SecretFilledDescription = "Filled from the export file into the value Squid holds empty. The value is never shown.";

    /// <summary>The release ids whose notes an Update would rewrite: imported before, notes changed (same rule as the preview).</summary>
    public static IReadOnlyList<int> ReleaseIdsToCompare(IEnumerable<OctopusResourceNode> nodes, OctopusImportPriorMappings prior)
        => nodes
            .Where(n => n.Kind == OctopusResourceKind.Release)
            .Select(n => OctopusImportPriorMappingPolicy.Decide(n, prior, null))
            .Where(d => d?.Action == Squid.Message.Enums.OctopusImport.OctopusImportPreviewAction.Update && d.DestinationId != null)
            .Select(d => d.DestinationId.Value)
            .Distinct()
            .ToList();

    /// <summary>The task ids of history units an Update would rewrite: the stored task row of every imported deployment whose task changed.</summary>
    public static IReadOnlyList<int> TaskIdsToCompare(IReadOnlyList<OctopusResourceNode> nodes, OctopusImportPriorMappings prior)
    {
        var tasks = nodes.Where(n => n.Kind == OctopusResourceKind.ServerTask).GroupBy(n => n.SourceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return nodes
            .Where(n => n.Kind == OctopusResourceKind.Deployment)
            .Select(n => ChangedTaskRow(n, tasks, prior))
            .Where(id => id != null)
            .Select(id => id.Value)
            .Distinct()
            .ToList();
    }

    /// <summary>What a release Update writes: its notes, Squid's current notes → the incoming ones.</summary>
    public static List<OctopusImportFieldChangeDto> ForRelease(OctopusReleaseDto incoming, int? destinationId, OctopusImportStoredHistory stored)
    {
        var storedNotes = destinationId != null && stored.ReleaseNotes.TryGetValue(destinationId.Value, out var notes) ? notes : null;
        var changes = new List<OctopusImportFieldChangeDto>();

        AddIfDifferent(changes, ReleaseNotesField, Trimmed(storedNotes), Trimmed(incoming?.ReleaseNotes));

        return changes.Count > 0 ? changes : [RecordIncomplete()];
    }

    /// <summary>
    /// What a history unit Update writes: the task's scalars that differ and, when the incoming details differ from the stored ones, the
    /// details. A task row no longer in Squid is written again as a whole.
    /// </summary>
    public static List<OctopusImportFieldChangeDto> ForHistoryUnit(OctopusServerTaskDto incoming, OctopusImportPriorMapping storedUnit, int? storedTaskId, OctopusImportStoredHistory stored)
    {
        if (storedTaskId == null || !stored.Tasks.TryGetValue(storedTaskId.Value, out var storedTask)) return [new OctopusImportFieldChangeDto { Field = TaskField, Description = TaskRowMissingDescription }];

        var changes = TaskScalarChanges(incoming, storedTask);

        if (DetailsChanged(incoming, storedUnit)) changes.Add(new OctopusImportFieldChangeDto { Field = DetailsField, Description = DetailsChangedDescription });

        return changes.Count > 0 ? changes : [RecordIncomplete()];
    }

    /// <summary>History moved onto an owner this import creates again: its content changes are listed only when they were read for comparison.</summary>
    public static List<OctopusImportFieldChangeDto> Relinked(IEnumerable<OctopusImportFieldChangeDto> contentChanges)
        => [.. contentChanges ?? [], new OctopusImportFieldChangeDto { Field = OwnerField, Description = RelinkedDescription }];

    /// <summary>
    /// What a fill writes: the secret field, named only; for a feed also the file's username, which the fill writes where Squid's is
    /// empty (a plain field, old → new).
    /// </summary>
    public static List<OctopusImportFieldChangeDto> ForSecretFill(OctopusResourceKind kind, string squidUsername = null, string fillUsername = null)
    {
        var changes = new List<OctopusImportFieldChangeDto> { new() { Field = kind == OctopusResourceKind.Feed ? FeedPasswordField : VariableValueField, ValueHidden = true, Description = SecretFilledDescription } };

        if (kind == OctopusResourceKind.Feed && !string.IsNullOrEmpty(fillUsername)) AddIfDifferent(changes, FeedUsernameField, Trimmed(squidUsername), fillUsername);

        return changes;
    }

    private static int? ChangedTaskRow(OctopusResourceNode deployment, IReadOnlyDictionary<string, OctopusResourceNode> tasks, OctopusImportPriorMappings prior)
    {
        var taskId = deployment.GetSource<OctopusDeploymentDto>()?.TaskId;

        if (string.IsNullOrWhiteSpace(taskId) || !tasks.TryGetValue(taskId, out var task)) return null;
        if (!prior.TryGet(OctopusResourceKind.Deployment.ToString(), deployment.SourceId, out var storedUnit)) return null;
        if (!prior.TryGet(OctopusResourceKind.ServerTask.ToString(), task.SourceId, out var storedTask)) return null;

        var action = OctopusImportPriorMappingPolicy.DecideHistoryUnit(storedUnit, task.GetSource<OctopusServerTaskDto>() ?? new OctopusServerTaskDto());

        return action == Squid.Message.Enums.OctopusImport.OctopusImportPreviewAction.Update ? storedTask.DestinationId : null;
    }

    // The same values the importer writes (state normalised, times in UTC), compared at the fingerprint's precision.
    private static List<OctopusImportFieldChangeDto> TaskScalarChanges(OctopusServerTaskDto incoming, OctopusImportStoredTask stored)
    {
        var changes = new List<OctopusImportFieldChangeDto>();

        AddIfDifferent(changes, StateField, stored.State, OctopusImportTaskStateNormalizer.Normalize(incoming?.State).State);
        AddIfDifferent(changes, QueueTimeField, Time(stored.QueueTime), incoming?.QueueTime == null ? Time(stored.QueueTime) : Time(incoming.QueueTime));
        AddIfDifferent(changes, StartTimeField, Time(stored.StartTime), Time(incoming?.StartTime));
        AddIfDifferent(changes, CompletedTimeField, Time(stored.CompletedTime), Time(incoming?.CompletedTime));
        AddIfDifferent(changes, ErrorMessageField, Trimmed(stored.ErrorMessage), Trimmed(incoming?.ErrorMessage));
        AddIfDifferent(changes, HasWarningsOrErrorsField, Flag(stored.HasWarningsOrErrors), Flag(incoming?.HasWarningsOrErrors == true));

        return changes;
    }

    // Details are monotonic (as in the policy): an incoming task without details never changes the stored ones.
    private static bool DetailsChanged(OctopusServerTaskDto incoming, OctopusImportPriorMapping storedUnit)
        => !string.IsNullOrWhiteSpace(incoming?.DetailFingerprint) && !string.Equals(storedUnit?.DetailFingerprint, incoming.DetailFingerprint, StringComparison.Ordinal);

    private static void AddIfDifferent(List<OctopusImportFieldChangeDto> changes, string field, string oldValue, string newValue)
    {
        if (string.Equals(oldValue ?? string.Empty, newValue ?? string.Empty, StringComparison.Ordinal)) return;

        changes.Add(new OctopusImportFieldChangeDto { Field = field, OldValue = Display(oldValue), NewValue = Display(newValue) });
    }

    private static OctopusImportFieldChangeDto RecordIncomplete() => new() { Field = RecordField, Description = RecordIncompleteDescription };

    private static string Display(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;

        return value.Length <= MaxDisplayLength ? value : value[..MaxDisplayLength] + "…";
    }

    private static string Trimmed(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Flag(bool value) => value ? "true" : "false";

    private static string Time(DateTimeOffset? value)
        => value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
