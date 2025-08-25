using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Response;
using Squid.Message.Enums;

namespace Squid.Message.Commands.Deployments.Variable;

public class CreateVariableSetCommand : ICommand
{
    public int OwnerId { get; set; }

    public VariableSetOwnerType OwnerType { get; set; }

    public int SpaceId { get; set; }

    public List<VariableDto> Variables { get; set; } = new List<VariableDto>();
}

public class CreateVariableSetResponse : SquidResponse<CreateVariableSetResponseData>
{
}

public class CreateVariableSetResponseData
{
    public VariableSetDto VariableSet { get; set; }
}
