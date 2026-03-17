using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Message.Requests.Deployments.Variable;

[RequiresPermission(Permission.VariableView)]
public class GetVariableSetsRequest : IPaginatedRequest
{
    public int PageIndex { get; set; } = 1;

    public int PageSize { get; set; } = 20;

    public VariableSetOwnerType? OwnerType { get; set; }

    public int? OwnerId { get; set; }

    public int? SpaceId { get; set; }

    public string Keyword { get; set; }
}

public class GetVariableSetsResponse : SquidResponse<GetVariableSetsResponseData>
{
}

public class GetVariableSetsResponseData
{
    public int Count { get; set; }

    public List<VariableSetDto> VariableSets { get; set; } = new List<VariableSetDto>();
}
