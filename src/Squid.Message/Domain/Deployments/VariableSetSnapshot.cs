namespace Squid.Message.Domain.Deployments;

public class VariableSetSnapshot : IEntity<int>
{
    public int Id { get; set; }

    public byte[] SnapshotData { get; set; }

    public string ContentHash { get; set; }

    public string CompressionType { get; set; } = "GZIP";

    public int UncompressedSize { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string CreatedBy { get; set; }
}
