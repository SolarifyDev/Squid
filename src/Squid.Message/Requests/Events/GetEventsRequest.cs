using Squid.Message.Attributes;
using Squid.Message.Contracts;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Requests.Events;

/// <summary>
/// Reads the persisted audit-event ("history") feed, filtered to a document
/// (release / project / deployment / environment / machine) and keyset-paginated
/// by the monotonic event id (newest first).
/// </summary>
[RequiresPermission(Permission.ProjectView)]
public class GetEventsRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }

    // Optional first-class document filters (any combination; all map to indexed columns).
    public int? ProjectId { get; set; }
    public int? ReleaseId { get; set; }
    public int? DeploymentId { get; set; }
    public int? EnvironmentId { get; set; }
    public int? MachineId { get; set; }

    /// <summary>Keyset cursor — return events with Id &lt; this (the next, older page). Null = newest page.</summary>
    public long? BeforeId { get; set; }

    /// <summary>Requested page size; the server clamps it to a sane maximum.</summary>
    public int Take { get; set; } = 30;
}

public class GetEventsResponse : SquidResponse<GetEventsResponseData>
{
}

public class GetEventsResponseData
{
    public List<EventDto> Events { get; set; } = new();

    /// <summary>Pass as <see cref="GetEventsRequest.BeforeId"/> to fetch the next (older) page; null when there are no more.</summary>
    public long? NextCursor { get; set; }

    public bool HasMore { get; set; }
}

public class EventDto
{
    public long Id { get; set; }
    public int Category { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string MessageTemplate { get; set; } = string.Empty;
    public string ReferencesJson { get; set; } = "{}";
    public int SpaceId { get; set; }
    public int? ProjectId { get; set; }
    public int? ReleaseId { get; set; }
    public int? DeploymentId { get; set; }
    public int? EnvironmentId { get; set; }
    public int? MachineId { get; set; }
    public int? ServerTaskId { get; set; }
    public int? UserId { get; set; }
    public string Username { get; set; } = "system";
    public int EstablishedWith { get; set; }
    public string EstablishedWithName { get; set; } = string.Empty;
    public string? UserAgent { get; set; }
    public DateTimeOffset Occurred { get; set; }
}
