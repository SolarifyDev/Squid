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
}

public class CreateReleaseResponse : SquidResponse<ReleaseDto>
{
}

public class CreateReleaseSelectedPackageDto
{
    public string ActionName { get; set; }
    
    public string Version { get; set; }
    
    public string PackageReferenceName { get; set; }
}
