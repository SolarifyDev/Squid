using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Variable;

[RequiresPermission(Permission.VariableEdit)]
public class CreateVariableSetCommand : ICommand
{
    public string Name { get; set; }

    public string Description { get; set; }

    public int OwnerId { get; set; }

    public VariableSetOwnerType OwnerType { get; set; }

    public int SpaceId { get; set; }

    public List<VariableModel> Variables { get; set; } = new List<VariableModel>();
}

public class CreateVariableSetResponse : SquidResponse<CreateVariableSetResponseData>
{
}

public class CreateVariableSetResponseData
{
    public VariableSetDto VariableSet { get; set; }
}
