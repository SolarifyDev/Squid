using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Requests.Machines;

/// <summary>
/// Read-only fetch of a machine's REAL Halibut connection log — the actual
/// transport-level events the server's Halibut runtime recorded for this
/// agent's endpoint (opening a new connection, TLS/security negotiation,
/// message exchange, listener accept, errors). This is the genuine "why is
/// this agent connected / why did it fail to connect" trace, as opposed to the
/// <see cref="MachineHealthStatus"/> summary which only reflects whether the
/// last Capabilities health check succeeded.
///
/// <para>Surfaced so an operator investigating a flaky / disconnected agent can
/// see the connection narrative without SSHing to the host or reading server
/// logs. Most informative for listening tentacles (the server dials the agent,
/// so connect attempts + failures are logged under the agent's URI); polling
/// agents dial in, so their server-side connection events may be sparser.</para>
///
/// <para>Empty list when: the agent has never connected, the server pod
/// restarted recently (the log is an in-memory recent-events buffer, not
/// persisted), or the machine's endpoint JSON can't be resolved to a Halibut
/// URI.</para>
/// </summary>
[RequiresPermission(Permission.MachineView)]
public class GetMachineConnectionLogRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public int MachineId { get; set; }

    /// <summary>
    /// Cap on how many of the most-recent connection-log entries to return
    /// (chronological order, oldest-to-newest within the cap). Defaults to
    /// <see cref="DefaultMaxEntries"/>; values outside [1, MaxAllowedEntries]
    /// are clamped server-side.
    /// </summary>
    public int? MaxEntries { get; set; }

    public const int DefaultMaxEntries = 200;

    public const int MaxAllowedEntries = 1000;
}

public class GetMachineConnectionLogResponse : SquidResponse<GetMachineConnectionLogResponseData>
{
}

public class GetMachineConnectionLogResponseData
{
    public int MachineId { get; set; }

    /// <summary>
    /// The Halibut endpoint URI the connection log is keyed under
    /// (<c>poll://{subscriptionId}/</c> or <c>https://host:port/</c>), or empty
    /// when the endpoint JSON couldn't be resolved. Surfaced so operators can
    /// confirm which endpoint the events belong to.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    public List<MachineConnectionLogEntry> Entries { get; set; } = new();
}

/// <summary>
/// One Halibut connection-log event projected for the API. Mirrors
/// <c>Halibut.Diagnostics.LogEvent</c> without leaking the Halibut type past
/// the service layer.
/// </summary>
public class MachineConnectionLogEntry
{
    /// <summary>When the event occurred (from Halibut's LogEvent.Time).</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// Halibut event category, e.g. <c>OpeningNewConnection</c>,
    /// <c>SecurityNegotiation</c>, <c>MessageExchange</c>,
    /// <c>ListenerAcceptedClient</c>, <c>Error</c>.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Human-readable event message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Concise error detail (the exception message) when the event carried an
    /// error, else empty. Full stack traces are intentionally omitted — this is
    /// an operator-facing connection narrative, not a server crash dump.
    /// </summary>
    public string Error { get; set; } = string.Empty;
}
