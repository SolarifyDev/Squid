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

    // Key binding: the Octopus user the API key belongs to, and an HMAC of the key under the server's master key (never the key)
    public string UserId { get; set; }
    public string KeyHmac { get; set; }

    // Cursor
    public List<string> ProjectIds { get; set; } = [];
    public int ProjectIndex { get; set; }
    public OctopusHistoryFetchPhase Phase { get; set; }
    public int Skip { get; set; }

    /// <summary>The catalog kind being paged (an index into the fetcher's fixed kind list) while <see cref="Phase"/> is Catalog.</summary>
    public int CatalogIndex { get; set; }

    /// <summary>The total the current catalog kind reported on its first page; a smaller total later restarts the kind.</summary>
    public int? CatalogTotalResults { get; set; }

    /// <summary>Resources referenced by fetched documents but not staged yet (<c>Kind|Id</c>); the Closure phase fetches them.</summary>
    public List<string> ClosureReferences { get; set; } = [];

    /// <summary>How often the chunk deadline abandoned the item at a cursor position; the item is skipped at the limit.</summary>
    public Dictionary<string, int> AbandonCounts { get; set; } = [];

    // Progress
    public OctopusHistoryFetchCountsDto Counts { get; set; } = new();

    /// <summary>The items left out, oldest first; the list is capped, the counts are not.</summary>
    public List<OctopusHistoryFetchSkippedItemDto> SkippedItems { get; set; } = [];
    public long TotalBytes { get; set; }
    public bool Done { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // Chunk lease
    public Guid? ChunkId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}
