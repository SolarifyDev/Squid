using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

public class OctopusImportSessionDto
{
    public Guid SessionId { get; set; }

    public int DestinationSpaceId { get; set; }

    public int OwnerUserId { get; set; }

    public OctopusImportSessionState State { get; set; }

    public OctopusImportSourceSummaryDto SourceSummary { get; set; }

    public OctopusImportSessionResultDto Result { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The latest the session can ever be kept until (its creation plus the configured maximum age); activity never extends it.</summary>
    public DateTimeOffset MaxExpiresAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset LastStateChangedAt { get; set; }
}
