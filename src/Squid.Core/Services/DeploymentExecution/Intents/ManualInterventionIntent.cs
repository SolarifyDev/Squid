namespace Squid.Core.Services.DeploymentExecution.Intents;

/// <summary>
/// Intent for a step-level manual intervention action. Unlike script-producing intents,
/// this intent is a "marker" describing pause-for-human semantics — renderers treat it as
/// a no-op because the actual suspension flow (interruption creation, server-task state
/// transition, resume) lives in
/// <see cref="Handlers.ManualInterventionActionHandler.ExecuteStepLevelAsync"/>. The intent
/// exists so every action in the pipeline has a uniform description surface, and so the
/// UI / audit layer can reason about what the manual step asked for without parsing raw
/// <c>Squid.Action.Manual.*</c> property names.
/// </summary>
public sealed record ManualInterventionIntent : ExecutionIntent
{
    /// <summary>
    /// The human-readable instructions shown to the approver. Corresponds to the legacy
    /// <c>"Squid.Action.Manual.Instructions"</c> property. Empty when the author did not
    /// supply any instructions (the step still pauses; there is just nothing to display).
    /// </summary>
    public string Instructions { get; init; } = string.Empty;

    /// <summary>
    /// Optional comma-separated list of team IDs responsible for resolving the intervention.
    /// Corresponds to the legacy <c>"Squid.Action.Manual.ResponsibleTeamIds"</c> property.
    /// <c>null</c> means the property was not set; an empty string means "explicitly empty"
    /// (any team may resolve).
    /// </summary>
    public string? ResponsibleTeamIds { get; init; }
}
