using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// The live progress of one confirmation, in memory: the import calls it per item (a lock, a few counters, a ring-buffer append, no I/O)
/// and the progress flusher snapshots it on its own schedule. Every change bumps <see cref="Version"/>, so an unchanged progress is never
/// written twice.
/// </summary>
public sealed class OctopusImportConfirmationProgressTracker
{
    public const int MaxRecentLogEntries = 20;
    public const int MaxDisplayNameLength = 200;

    private readonly object _lock = new();
    private readonly Guid _attemptId;
    private readonly IReadOnlyList<(OctopusImportConfirmationPhase Phase, int Total)> _totals;
    private readonly int[] _done;
    private readonly IReadOnlyDictionary<string, string> _projectNames;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _startedAt;
    private readonly Queue<OctopusImportConfirmationLogEntryDto> _log = new();

    private int _attempt = 1;
    private OctopusImportConfirmationStage _stage = OctopusImportConfirmationStage.Validating;
    private int _itemsDone;
    private OctopusImportConfirmationItemDto _current;
    private bool _finished;
    private DateTimeOffset _updatedAt;

    public OctopusImportConfirmationProgressTracker(Guid attemptId, IReadOnlyList<OctopusResourceNode> ordered, IReadOnlyDictionary<string, string> projectNames, TimeProvider time)
    {
        _attemptId = attemptId;
        _totals = OctopusImportConfirmationProgressPlan.Totals(ordered ?? []);
        _done = new int[_totals.Count];
        _projectNames = projectNames ?? new Dictionary<string, string>();
        _time = time ?? TimeProvider.System;
        _startedAt = _updatedAt = _time.GetUtcNow();
    }

    public long Version { get; private set; }

    public int ItemsTotal => _totals.Sum(t => t.Total);

    // ── Changes ──────────────────────────────────────────────────────────────────────────

    /// <summary>A transient retry imports the whole plan again: the count starts again from zero.</summary>
    public void BeginAttempt(int attempt) => Change(() =>
    {
        _attempt = attempt;
        _itemsDone = 0;
        _current = null;
        _finished = false;
        Array.Clear(_done);
        _log.Clear();
    });

    public void EnterStage(OctopusImportConfirmationStage stage) => Change(() => SetStage(stage));

    public void BeginItem(OctopusResourceNode resource)
    {
        if (OctopusImportConfirmationProgressPlan.PhaseOf(resource.Kind) is not { } phase) return;

        Change(() => _current = Item(resource, phase));
    }

    /// <summary>Counts the item (also one an earlier pass already completed, or the run never reaches its total) and logs its outcome.</summary>
    public void CompleteItem(OctopusResourceNode resource, OctopusImportResourceOutcomeState? outcome)
    {
        if (OctopusImportConfirmationProgressPlan.PhaseOf(resource.Kind) is not { } phase) return;

        Change(() => CountItem(resource, phase, outcome));
    }

    /// <summary>Ends the run in <paramref name="stage"/>; ending it again in the same stage changes nothing.</summary>
    public void Finish(OctopusImportConfirmationStage stage)
    {
        lock (_lock)
        {
            if (_finished && _stage == stage) return;
        }

        Change(() => FinishIn(stage));
    }

    // ── Snapshot ─────────────────────────────────────────────────────────────────────────

    public (long Version, OctopusImportConfirmationProgressDto Progress) Snapshot()
    {
        lock (_lock)
        {
            return (Version, new OctopusImportConfirmationProgressDto { AttemptId = _attemptId, Attempt = _attempt, Stage = _stage, ItemsDone = _itemsDone, ItemsTotal = ItemsTotal, Phases = PhaseSnapshot(), Current = CopyOf(_current), RecentLog = [.. _log.Select(CopyOf)], Finished = _finished, StartedAt = _startedAt, UpdatedAt = _updatedAt });
        }
    }

    // ── Internals (called under the lock) ────────────────────────────────────────────────

    private void Change(Action change)
    {
        lock (_lock)
        {
            change();
            _updatedAt = _time.GetUtcNow();
            Version++;
        }
    }

    private void SetStage(OctopusImportConfirmationStage stage)
    {
        _stage = stage;

        if (stage != OctopusImportConfirmationStage.Importing) _current = null;

        Append(new OctopusImportConfirmationLogEntryDto { At = _time.GetUtcNow(), Stage = stage });
    }

    private void FinishIn(OctopusImportConfirmationStage stage)
    {
        _finished = true;

        SetStage(stage);
    }

    private void CountItem(OctopusResourceNode resource, OctopusImportConfirmationPhase phase, OctopusImportResourceOutcomeState? outcome)
    {
        _itemsDone++;
        _done[PhaseIndex(phase)]++;
        _current = null;

        Append(new OctopusImportConfirmationLogEntryDto { At = _time.GetUtcNow(), Stage = _stage, Phase = phase, Kind = resource.Kind.ToString(), Name = Truncate(resource.Name), ProjectName = ProjectNameOf(resource), Outcome = outcome });
    }

    private OctopusImportConfirmationItemDto Item(OctopusResourceNode resource, OctopusImportConfirmationPhase phase)
        => new() { Phase = phase, Kind = resource.Kind.ToString(), Name = Truncate(resource.Name), ProjectName = ProjectNameOf(resource), Index = _itemsDone + 1 };

    private void Append(OctopusImportConfirmationLogEntryDto entry)
    {
        _log.Enqueue(entry);

        while (_log.Count > MaxRecentLogEntries) _log.Dequeue();
    }

    private int PhaseIndex(OctopusImportConfirmationPhase phase)
    {
        for (var i = 0; i < _totals.Count; i++)
            if (_totals[i].Phase == phase) return i;

        throw new InvalidOperationException($"Phase {phase} is not in the confirmation plan.");
    }

    private List<OctopusImportConfirmationPhaseProgressDto> PhaseSnapshot() => _totals.Select((t, i) => new OctopusImportConfirmationPhaseProgressDto { Phase = t.Phase, Done = _done[i], Total = t.Total }).ToList();

    private string ProjectNameOf(OctopusResourceNode resource)
        => resource.OwnerProjectId != null && _projectNames.TryGetValue(resource.OwnerProjectId, out var name) ? Truncate(name) : null;

    public static string Truncate(string name) => name == null || name.Length <= MaxDisplayNameLength ? name : string.Concat(name.AsSpan(0, MaxDisplayNameLength - 1), "…");

    private static OctopusImportConfirmationItemDto CopyOf(OctopusImportConfirmationItemDto item)
        => item == null ? null : new OctopusImportConfirmationItemDto { Phase = item.Phase, Kind = item.Kind, Name = item.Name, ProjectName = item.ProjectName, Index = item.Index };

    private static OctopusImportConfirmationLogEntryDto CopyOf(OctopusImportConfirmationLogEntryDto entry)
        => new() { At = entry.At, Stage = entry.Stage, Phase = entry.Phase, Kind = entry.Kind, Name = entry.Name, ProjectName = entry.ProjectName, Outcome = entry.Outcome };
}
