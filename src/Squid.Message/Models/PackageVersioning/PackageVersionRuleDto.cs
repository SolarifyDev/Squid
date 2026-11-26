namespace Squid.Message.Models.PackageVersioning;

public class PackageVersionRuleDto
{
    public int FeedId { get; set; }

    public string PackageId { get; set; }

    public string VersionTemplate { get; set; }

    /// <summary>True when the package has no rule of its own and <see cref="VersionTemplate"/> is the default.</summary>
    public bool IsDefault { get; set; }
}
