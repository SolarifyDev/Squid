using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Variable;

[RequiresPermission(Permission.VariableEdit)]
public class UpdateVariableSetCommand : ICommand, ISpaceScoped
{
    public int Id { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public int OwnerId { get; set; }

    public VariableSetOwnerType OwnerType { get; set; }

    public int SpaceId { get; set; }
    int? ISpaceScoped.SpaceId => SpaceId;

    public List<VariableModel> Variables { get; set; } = new List<VariableModel>();
}

public class UpdateVariableSetResponse : SquidResponse<UpdateVariableSetResponseData>
{
}

public class UpdateVariableSetResponseData
{
    public VariableSetDto VariableSet { get; set; }
}
