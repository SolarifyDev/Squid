using Squid.Message.Enums.Events;

namespace Squid.Message.Models.Events;

/// <summary>
/// Input to <c>IEventService.RecordAsync</c> — consolidates the event fields into
/// a single record (the call has more than the 5-parameter cap). Provenance
/// (user / established-with / user-agent) is resolved by the service from the
/// ambient request context, NOT supplied here.
/// </summary>
public sealed record RecordEventRequest
{
    public required EventCategory Category { get; init; }

    public required int SpaceId { get; init; }

    public int? ProjectId { get; init; }
    public int? ReleaseId { get; init; }
    public int? DeploymentId { get; init; }
    public int? EnvironmentId { get; init; }
    public int? MachineId { get; init; }
    public int? ServerTaskId { get; init; }

    /// <summary>Structured message arguments; serialized to the jsonb references column by the service.</summary>
    public object? References { get; init; }
}
