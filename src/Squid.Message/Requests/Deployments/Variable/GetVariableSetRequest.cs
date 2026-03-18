using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Message.Requests.Deployments.Variable;

[RequiresPermission(Permission.VariableView)]
public class GetVariableSetRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int Id { get; set; }
}

public class GetVariableSetResponse : SquidResponse<GetVariableSetResponseData>
{
}

public class GetVariableSetResponseData
{
    public VariableSetDto VariableSet { get; set; }
}
