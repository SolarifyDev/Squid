using Squid.Message.Domain;

namespace Squid.Core.Persistence.Data.Domain.Deployments;

public class ProcessSnapshot : IEntity<int>
{
    public int Id { get; set; }

    public int OriginalProcessId { get; set; }

    public int Version { get; set; }

    public byte[] SnapshotData { get; set; }

    public string ContentHash { get; set; }

    public string CompressionType { get; set; } = "GZIP";

    public int UncompressedSize { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string CreatedBy { get; set; }
}
