using Squid.Message.Models.Deployments.Environment;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Environment;

public class UpdateEnvironmentCommand : ICommand
{
    public int Id { get; set; }

    public int SpaceId { get; set; }

    public string Slug { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public int SortOrder { get; set; }

    public bool UseGuidedFailure { get; set; }

    public bool AllowDynamicInfrastructure { get; set; }
}

public class UpdateEnvironmentResponse : SquidResponse<UpdateEnvironmentResponseData>
{
}

public class UpdateEnvironmentResponseData
{
    public EnvironmentDto Environment { get; set; }
}
