namespace Squid.Core.Persistence.Entities.Deployments;

/// <summary>One version of a package pushed to a space's built-in feed. The file itself lives in a package store under <see cref="BlobKey"/>.</summary>
public class BuiltInPackage : IEntity<int>
{
    public int Id { get; set; }

    public int SpaceId { get; set; }

    public int FeedId { get; set; }

    public string PackageId { get; set; }

    public string Version { get; set; }

    public string FileExtension { get; set; }

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; }

    public int StoreId { get; set; }

    public string BlobKey { get; set; }

    public DateTimeOffset PublishedDate { get; set; }

    public int PublishedBy { get; set; }

    public string Source { get; set; }
}
