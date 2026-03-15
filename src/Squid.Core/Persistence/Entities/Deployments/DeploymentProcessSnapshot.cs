namespace Squid.Core.Persistence.Entities.Deployments;

public class DeploymentProcessSnapshot : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public int OriginalProcessId { get; set; }

    public int Version { get; set; }

    public byte[] SnapshotData { get; set; }

    public string ContentHash { get; set; }

    public string CompressionType { get; set; } = "GZIP";

    public int UncompressedSize { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
