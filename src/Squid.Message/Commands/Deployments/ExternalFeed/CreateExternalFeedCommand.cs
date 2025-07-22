using System.Text.Json.Serialization;
using Squid.Message.Models.Deployments.ExternalFeed;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ExternalFeed;

public class CreateExternalFeedCommand : ICommand
{
    public string FeedType { get; set; }

    public string ApiVersion { get; set; }

    public string RegistryPath { get; set; }

    public string FeedUri { get; set; }

    public string Username { get; set; }
    
    public string Password { get; set; }

    public string Name { get; set; }

    public string Slug { get; set; }

    public List<string> PackageAcquisitionLocationOptions { get; set; }

    public Guid SpaceId { get; set; }
}

public class CreateExternalFeedResponse : SquidResponse<CreateExternalFeedResponseData>
{
}

public class CreateExternalFeedResponseData
{
    public ExternalFeedDto ExternalFeed { get; set; }
} 