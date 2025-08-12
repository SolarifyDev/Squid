using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Variable;

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
