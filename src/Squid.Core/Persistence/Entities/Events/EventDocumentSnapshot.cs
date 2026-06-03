namespace Squid.Core.Persistence.Entities.Events;

/// <summary>
/// Side table holding the document JSON snapshot for an audit event
/// (DocumentCreated/Modified/Deleted). Kept OUT of the hot <see cref="Event"/>
/// table so the audit feed stays lean and fast to scan/index; loaded lazily only
/// when an operator expands an event to view the snapshot/diff.
///
/// <para>Postgres TOAST transparently compresses the jsonb. For Modified events
/// the pair (<see cref="BeforeJson"/>/<see cref="AfterJson"/>) may store a
/// compact diff. Given as its own table it can also carry a SHORTER retention
/// than the event metadata (snapshots are the bulk of the volume).</para>
/// </summary>
public class EventDocumentSnapshot : IEntity<long>
{
    public long Id { get; set; }

    /// <summary>The <see cref="Event"/> this snapshot belongs to (1:1, unique).</summary>
    public long EventId { get; set; }

    public string? BeforeJson { get; set; }

    public string? AfterJson { get; set; }
}
