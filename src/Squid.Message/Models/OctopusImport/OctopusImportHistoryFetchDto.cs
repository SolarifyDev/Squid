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

    /// <summary>The server's base URL (scheme, host and path; never credentials, a query or a fragment), so a reopened page can resume the read.</summary>
    public string ServerUrl { get; set; }

    /// <summary>Up to five names of the chosen projects, recorded while reading; display only.</summary>
    public List<string> ChosenProjectNames { get; set; } = [];

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

    /// <summary>The total the current project's releases or deployments reported on the last page read; cleared when the phase moves on.</summary>
    public int? PhaseTotalResults { get; set; }

    /// <summary>The Config-as-Code projects read so far, so a project read again after a catalog restart is counted once.</summary>
    public List<string> ConfigAsCodeProjectIds { get; set; } = [];

    /// <summary>Resources referenced by fetched documents but not staged yet (<c>Kind|Id</c>); the Closure phase fetches them.</summary>
    public List<string> ClosureReferences { get; set; } = [];

    /// <summary>How many chunks failed on the item at a cursor position (deadline abandoned it, or the server failed its request); the item is skipped at the limit.</summary>
    public Dictionary<string, int> AbandonCounts { get; set; } = [];

    /// <summary>
    /// The id the first chunk stored with the session's staged documents; every later chunk finds the same id stored or refuses (the
    /// staging is gone: the session was cleaned up, or an earlier Squid kept it on one server's disk). Null on a cursor no chunk has run yet.
    /// </summary>
    public string StagingId { get; set; }

    /// <summary>Per kind, how many documents committed items stored for the first time; bundling refuses when fewer are stored.</summary>
    public Dictionary<string, int> StagedDocumentCounts { get; set; } = [];

    /// <summary>How many tasks' details committed items stored for the first time; bundling refuses when fewer are stored.</summary>
    public int StagedDetails { get; set; }

    // Progress
    public OctopusHistoryFetchCountsDto Counts { get; set; } = new();

    /// <summary>The items left out, oldest first; the list is capped, the counts are not.</summary>
    public List<OctopusHistoryFetchSkippedItemDto> SkippedItems { get; set; } = [];
    /// <summary>Bytes read from the server so far, every read counted (the progress shows it); never capped across chunks.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Bytes of task details stored beside the bundle; capped like the staged documents, which the bundle writer counts.</summary>
    public long DetailsBytes { get; set; }
    public bool Done { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // Chunk lease
    public Guid? ChunkId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}
