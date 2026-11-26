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

    /// <summary>SHA-256 of the archive file; names the blob and guards a deployment against a file the store altered.</summary>
    public string Sha256 { get; set; }

    /// <summary>SHA-256 over the sorted normalised paths and per-file content hashes, so the same files packed differently (another zip tool, a tar.gz) match. Null for rows pushed before it existed, which never dedupe.</summary>
    public string ContentFingerprint { get; set; }

    public int StoreId { get; set; }

    public string BlobKey { get; set; }

    public DateTimeOffset PublishedDate { get; set; }

    public int PublishedBy { get; set; }

    public string Source { get; set; }
}
