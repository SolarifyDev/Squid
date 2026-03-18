using System.Text.Json.Serialization;
using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Environment;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Environment;

[RequiresPermission(Permission.EnvironmentCreate)]
public class CreateEnvironmentCommand : ICommand, ISpaceScoped
{
    public int SpaceId { get; set; }
    int? ISpaceScoped.SpaceId => SpaceId;

    public string Slug { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public int SortOrder { get; set; }

    public bool UseGuidedFailure { get; set; }

    public bool AllowDynamicInfrastructure { get; set; }
}

public class CreateEnvironmentResponse : SquidResponse<CreateEnvironmentResponseData>
{
}

public class CreateEnvironmentResponseData
{
    public EnvironmentDto Environment { get; set; }
}
