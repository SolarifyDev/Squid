using System.Text.Json.Serialization;
using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Release;

[RequiresPermission(Permission.ReleaseCreate)]
public class CreateReleaseCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public string Version { get; set; }
    
    public int ChannelId { get; set; }
    
    public int ProjectId { get; set; }
    
    public string ReleaseNote { get; set; }

    public bool IgnoreChannelRules { get; set; }

    public List<CreateReleaseSelectedPackageDto> SelectedPackages { get; set; } = new();

    [JsonIgnore]
    public DateTimeOffset? HistoricalCreatedDate { get; set; }

    /// <summary>
    /// Set only by the Octopus history import: a historical release already passed Octopus's channel rules when it
    /// was created, so today's rules are not applied again. Never bound from the API.
    /// </summary>
    [JsonIgnore]
    public bool IsHistoricalImport { get; set; }
}

public class CreateReleaseResponse : SquidResponse<ReleaseDto>
{
}

public class CreateReleaseSelectedPackageDto
{
    public string ActionName { get; set; }

    public string Version { get; set; }

    public string PackageReferenceName { get; set; }

    public int FeedId { get; set; }
}
