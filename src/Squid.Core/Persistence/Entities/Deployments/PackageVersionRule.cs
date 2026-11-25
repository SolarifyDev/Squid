namespace Squid.Core.Persistence.Entities.Deployments;

public class PackageVersionRule : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public int FeedId { get; set; }

    public string PackageId { get; set; }

    public string VersionTemplate { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}
