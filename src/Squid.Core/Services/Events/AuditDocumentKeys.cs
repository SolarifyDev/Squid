namespace Squid.Core.Services.Events;

/// <summary>
/// The audit identity of a persisted document: which space it lives in, which first-class
/// Event FK columns it populates (so the event appears under the right document feeds),
/// and a human display name for the references blob. Produced by
/// <see cref="IAuditDocumentRegistry"/> per document type.
/// </summary>
public sealed record AuditDocumentKeys
{
    public required int SpaceId { get; init; }

    public int? ProjectId { get; init; }
    public int? ReleaseId { get; init; }
    public int? EnvironmentId { get; init; }
    public int? MachineId { get; init; }

    public string Name { get; init; }
}
