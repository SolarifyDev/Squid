namespace Squid.Core.Persistence.Entities.Deployments;

public class ExternalFeed : IEntity<int>, IAuditable
{
    public int Id { get; set; }

    public string FeedType { get; set; }

    public string Properties { get; set; }

    public string FeedUri { get; set; }

    public string Username { get; set; }
    
    public string Password { get; set; }

    public bool PasswordHasValue => !string.IsNullOrWhiteSpace(Password);

    public string Name { get; set; }

    public string Slug { get; set; }

    public string PackageAcquisitionLocationOptions { get; set; }

    public int SpaceId { get; set; }

    // IAuditable
    public DateTimeOffset CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public int LastModifiedBy { get; set; }
}