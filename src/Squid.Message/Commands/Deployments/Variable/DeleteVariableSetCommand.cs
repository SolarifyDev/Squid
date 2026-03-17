using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Variable;

[RequiresPermission(Permission.VariableEdit)]
public class DeleteVariableSetCommand : ICommand
{
    public int Id { get; set; }
}

public class DeleteVariableSetResponse : SquidResponse<DeleteVariableSetResponseData>
{
}

public class DeleteVariableSetResponseData
{
    public string Message { get; set; }
}
