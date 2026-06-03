using Squid.Message.Enums.Events;

namespace Squid.Message.Constants.Events;

/// <summary>
/// Single source of truth mapping each <see cref="EventCategory"/> to its
/// operator-facing display name + message template. The template carries
/// <c>{Placeholder}</c> tokens (Project / Release / Environment / Document) that
/// the UI renders + linkifies against the event's structured References — so the
/// fat English text is NOT duplicated into every persisted row.
///
/// <para>Every <see cref="EventCategory"/> MUST have a descriptor here; a drift
/// test pins that (see EventCategoryRegistryTests).</para>
/// </summary>
public static class EventCategoryRegistry
{
    public sealed record Descriptor(string DisplayName, string MessageTemplate);

    private static readonly IReadOnlyDictionary<EventCategory, Descriptor> Descriptors = new Dictionary<EventCategory, Descriptor>
    {
        [EventCategory.DocumentCreated] = new("Document created", "{Document} was created"),
        [EventCategory.DocumentModified] = new("Document modified", "{Document} was modified"),
        [EventCategory.DocumentDeleted] = new("Document deleted", "{Document} was deleted"),

        [EventCategory.DeploymentQueued] = new("Deployment queued", "Deployed {Release} to {Environment}"),
        [EventCategory.DeploymentStarted] = new("Deployment started", "Deploy to {Environment} started for {Release}"),
        [EventCategory.ManualInterventionRaised] = new("Manual intervention interruption raised", "Deploy to {Environment} requires manual intervention for {Release}"),
        [EventCategory.ManualInterventionSubmitted] = new("Manual intervention submitted", "Manual intervention submitted for deploy to {Environment} for {Release}"),
        [EventCategory.GuidedFailureRaised] = new("Guided failure interruption raised", "Deploy to {Environment} hit a guided failure for {Release}"),
        [EventCategory.DeploymentResumed] = new("Deployment resumed", "Deploy to {Environment} resumed for {Release}"),
        [EventCategory.DeploymentSucceeded] = new("Deployment succeeded", "Deploy to {Environment} succeeded for {Release}"),
        [EventCategory.DeploymentFailed] = new("Deployment failed", "Deploy to {Environment} failed for {Release}"),
        [EventCategory.DeploymentCanceled] = new("Deployment canceled", "Deploy to {Environment} canceled for {Release}")
    };

    public static Descriptor Describe(EventCategory category) =>
        Descriptors.TryGetValue(category, out var descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(category), category, "No registry descriptor for this event category");

    public static bool Has(EventCategory category) => Descriptors.ContainsKey(category);
}
