namespace Squid.Core.Persistence.Entities.Deployments;

/// <summary>
/// Persistent Octopus source id → Squid destination id provenance, one row per (space, source type, source id).
/// Read-only from EF: every write goes through <c>OctopusImportSourceMapDataProvider.UpsertAsync</c> (raw SQL),
/// so no document audit is ever captured for it.
/// </summary>
public class OctopusImportSourceMap : IEntity<long>
{
    public long Id { get; set; }

    public int SpaceId { get; set; }

    public string SourceType { get; set; }

    public string SourceId { get; set; }

    public int DestinationId { get; set; }

    public string OutcomeState { get; set; }

    public string Fingerprint { get; set; }

    public string DetailFingerprint { get; set; }

    public Guid? LastSessionId { get; set; }

    public DateTimeOffset CreatedDate { get; set; }

    public int CreatedBy { get; set; }

    public DateTimeOffset LastModifiedDate { get; set; }

    public int LastModifiedBy { get; set; }
}
