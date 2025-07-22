namespace Squid.Core.Domain.Deployments;

public class ExternalFeed : IEntity<Guid>
{
    public Guid Id { get; set; }

    public string FeedType { get; set; }

    public string ApiVersion { get; set; }

    public string RegistryPath { get; set; }

    public string FeedUri { get; set; }

    public string Username { get; set; }

    public bool PasswordHasValue { get; set; }

    public string Name { get; set; }

    public string Slug { get; set; }

    public List<string> PackageAcquisitionLocationOptions { get; set; }

    public Guid SpaceId { get; set; }

    public DateTime? LastModifiedOn { get; set; }

    public string LastModifiedBy { get; set; }
} 