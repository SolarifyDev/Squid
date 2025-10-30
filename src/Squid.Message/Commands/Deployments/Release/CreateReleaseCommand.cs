using Squid.Message.Models.Deployments.Release;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Release;

public class CreateReleaseCommand : ICommand
{
    public string Version { get; set; }
    
    public Guid ChannelId { get; set; }
    
    public Guid ProjectId { get; set; }
    
    public string ReleaseNote { get; set; }
    
    public CreateReleaseSelectedPackageDto SelectedPackages { get; set; }
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
