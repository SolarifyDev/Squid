namespace Squid.Message.Domain.Deployments;

public class ExternalFeed : IEntity<Guid>
{
    public Guid Id { get; set; }

    public string FeedType { get; set; }

    public string ApiVersion { get; set; }

    public string RegistryPath { get; set; }

    public string FeedUri { get; set; }

    public string Username { get; set; }
    
    public string Password { get; set; }

    public bool PasswordHasValue => !string.IsNullOrWhiteSpace(Password);

    public string Name { get; set; }

    public string Slug { get; set; }

    public string PackageAcquisitionLocationOptions { get; set; }

    public Guid SpaceId { get; set; }

    public DateTimeOffset? LastModifiedOn { get; set; }

    public string LastModifiedBy { get; set; }
}