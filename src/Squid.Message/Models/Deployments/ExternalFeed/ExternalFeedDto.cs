namespace Squid.Message.Models.Deployments.ExternalFeed;

public class ExternalFeedDto : IBaseModel
{
    public int Id { get; set; }

    public string FeedType { get; set; }

    public string ApiVersion { get; set; }

    public string RegistryPath { get; set; }

    public string FeedUri { get; set; }

    public string Username { get; set; }

    public bool PasswordHasValue { get; set; }

    public string Name { get; set; }

    public string Slug { get; set; }

    public List<string> PackageAcquisitionLocationOptions { get; set; }

    public int SpaceId { get; set; }

    public DateTimeOffset? LastModifiedOn { get; set; }

    public string LastModifiedBy { get; set; }
} 