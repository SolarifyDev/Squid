namespace Squid.Message.Models.Events;

/// <summary>
/// Structured reference arguments for a deployment-lifecycle audit event, serialized
/// to the Event's jsonb <c>references</c> column. Property names match the
/// <c>{Placeholder}</c> tokens in the category message templates (e.g.
/// "Deploy to {Environment} succeeded for {Release}") so the history UI can linkify
/// and render without a pre-baked English string.
/// </summary>
public sealed record DeploymentEventReferences
{
    public string? Project { get; init; }
    public string? Release { get; init; }
    public string? Environment { get; init; }
}
