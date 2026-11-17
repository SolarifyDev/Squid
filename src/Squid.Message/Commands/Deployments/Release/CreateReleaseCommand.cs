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

    /// <summary>
    /// Set only by the Octopus history import, for its second and later release of a project in one confirmation: the snapshots the
    /// first one took. The project's configuration does not change between them, so the same snapshots are reused instead of rebuilt.
    /// Honoured only with <see cref="IsHistoricalImport"/>. Never bound from the API.
    /// </summary>
    [JsonIgnore]
    public HistoricalReleaseSnapshots HistoricalSnapshots { get; set; }
}

/// <summary>The variable-set and deployment-process snapshots a historical release is created against.</summary>
public sealed record HistoricalReleaseSnapshots(int VariableSetSnapshotId, int DeploymentProcessSnapshotId);

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
