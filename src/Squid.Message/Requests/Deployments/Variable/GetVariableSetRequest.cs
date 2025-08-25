using Squid.Message.Response;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Message.Requests.Deployments.Variable;

public class GetVariableSetRequest : IRequest
{
    public int Id { get; set; }
}

public class GetVariableSetResponse : SquidResponse<GetVariableSetResponseData>
{
}

public class GetVariableSetResponseData
{
    public VariableSetDto VariableSet { get; set; }
}
