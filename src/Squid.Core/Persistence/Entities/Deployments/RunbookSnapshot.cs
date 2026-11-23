namespace Squid.Core.Persistence.Entities.Deployments;

public class RunbookSnapshot : IEntity<int>
{
    public int Id { get; set; }
    public int RunbookId { get; set; }
    public int Version { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public int PublishedBy { get; set; }
    public byte[] SnapshotData { get; set; }
    public string ContentHash { get; set; }
    public string CompressionType { get; set; } = "GZIP";
    public int UncompressedSize { get; set; }
}
