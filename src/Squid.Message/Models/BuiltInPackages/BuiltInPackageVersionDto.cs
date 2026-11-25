using Squid.Message.Models.Publishing;

namespace Squid.Message.Models.BuiltInPackages;

/// <summary>One stored version of a built-in feed package.</summary>
public class BuiltInPackageVersionDto
{
    public int Id { get; set; }

    public int FeedId { get; set; }

    public string PackageId { get; set; }

    public string Version { get; set; }

    public string FileExtension { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the file, lower-case hex.</summary>
    public string Sha256 { get; set; }

    public DateTimeOffset PublishedDate { get; set; }

    public int PublishedBy { get; set; }

    public string Source { get; set; }

    /// <summary>What inspection found in the package. Null when the version was pushed before inspection existed, and left out of version lists.</summary>
    public PackageFacts Facts { get; set; }
}
