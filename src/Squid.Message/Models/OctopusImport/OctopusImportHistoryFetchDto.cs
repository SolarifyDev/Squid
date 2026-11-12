using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

/// <summary>
/// The persisted state of a REST history fetch, kept on the session's source summary. The first chunk pins the
/// server, the Octopus space and the installation; the cursor (<see cref="ProjectIndex"/>, <see cref="Phase"/>,
/// <see cref="Skip"/>) lets the next chunk resume. A chunk holds the fetch through <see cref="ChunkId"/> until
/// <see cref="LeaseExpiresAt"/>, so two chunks never run against one session at the same time.
/// </summary>
public class OctopusImportHistoryFetchDto
{
    // Pins
    public string ServerHost { get; set; }
    public string OctopusSpaceId { get; set; }
    public string InstallationId { get; set; }
    public string OctopusVersion { get; set; }
    public bool Spaceless { get; set; }

    // Cursor
    public List<string> ProjectIds { get; set; } = [];
    public int ProjectIndex { get; set; }
    public OctopusHistoryFetchPhase Phase { get; set; }
    public int Skip { get; set; }

    // Progress
    public OctopusHistoryFetchCountsDto Counts { get; set; } = new();
    public long TotalBytes { get; set; }
    public bool Done { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // Chunk lease
    public Guid? ChunkId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}
