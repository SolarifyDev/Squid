namespace Squid.Message.Models.BuiltInPackages;

/// <summary>A package of the built-in feed: its id and how many versions it has.</summary>
public class BuiltInPackageDto
{
    public string PackageId { get; set; }

    public int VersionCount { get; set; }

    public DateTimeOffset LastPublishedDate { get; set; }
}
