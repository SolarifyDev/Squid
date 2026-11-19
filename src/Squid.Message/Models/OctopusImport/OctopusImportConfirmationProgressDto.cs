using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// Live progress of a confirmation attempt: written on its own connection while the import transaction runs, so it is readable before
/// the import commits, and kept once the attempt ends so its final state stays readable.
/// </summary>
public class OctopusImportConfirmationProgressDto
{
    public Guid AttemptId { get; set; }

    /// <summary>The in-place transient retry this progress counts, 1-based; a retry starts counting again from zero.</summary>
    public int Attempt { get; set; }

    public OctopusImportConfirmationStage Stage { get; set; }

    public int ItemsDone { get; set; }

    public int ItemsTotal { get; set; }

    /// <summary>Only phases the plan has items in, in plan order.</summary>
    public List<OctopusImportConfirmationPhaseProgressDto> Phases { get; set; } = [];

    /// <summary>The item being imported now; null outside <see cref="OctopusImportConfirmationStage.Importing"/>.</summary>
    public OctopusImportConfirmationItemDto Current { get; set; }

    /// <summary>The most recent entries, newest last, at most 20.</summary>
    public List<OctopusImportConfirmationLogEntryDto> RecentLog { get; set; } = [];

    public bool Finished { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public class OctopusImportConfirmationPhaseProgressDto
{
    public OctopusImportConfirmationPhase Phase { get; set; }

    public int Done { get; set; }

    public int Total { get; set; }
}

public class OctopusImportConfirmationItemDto
{
    public OctopusImportConfirmationPhase Phase { get; set; }

    /// <summary>The Octopus resource kind name (e.g. <c>Release</c>).</summary>
    public string Kind { get; set; }

    public string Name { get; set; }

    /// <summary>The Octopus project the item belongs to, when it belongs to one.</summary>
    public string ProjectName { get; set; }

    /// <summary>The item's position in the run, 1-based.</summary>
    public int Index { get; set; }
}

/// <summary>One finished item, or a stage change (then <see cref="Kind"/> and <see cref="Name"/> are null).</summary>
public class OctopusImportConfirmationLogEntryDto
{
    public DateTimeOffset At { get; set; }

    public OctopusImportConfirmationStage Stage { get; set; }

    public OctopusImportConfirmationPhase? Phase { get; set; }

    public string Kind { get; set; }

    public string Name { get; set; }

    public string ProjectName { get; set; }

    public OctopusImportResourceOutcomeState? Outcome { get; set; }
}
