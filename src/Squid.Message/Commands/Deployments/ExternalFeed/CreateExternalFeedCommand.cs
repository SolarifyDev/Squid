using System.Text.Json.Serialization;
using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.ExternalFeed;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ExternalFeed;

[RequiresPermission(Permission.FeedEdit)]
public class CreateExternalFeedCommand : ICommand, ISpaceScoped
{
    public string FeedType { get; set; }

    public Dictionary<string, string> Properties { get; set; }

    public string FeedUri { get; set; }

    public string Username { get; set; }
    
    public string Password { get; set; }

    public string Name { get; set; }

    public string Slug { get; set; }

    public List<string> PackageAcquisitionLocationOptions { get; set; }

    public int SpaceId { get; set; }
    int? ISpaceScoped.SpaceId => SpaceId;
}

public class CreateExternalFeedResponse : SquidResponse<CreateExternalFeedResponseData>
{
}

public class CreateExternalFeedResponseData
{
    public ExternalFeedDto ExternalFeed { get; set; }
} 