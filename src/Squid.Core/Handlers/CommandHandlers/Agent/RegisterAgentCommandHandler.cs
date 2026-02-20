using Squid.Core.Services.Agents;
using Squid.Message.Commands.Agent;

namespace Squid.Core.Handlers.CommandHandlers.Agent;

public class RegisterAgentCommandHandler : ICommandHandler<RegisterAgentCommand, RegisterAgentResponse>
{
    private readonly IAgentService _agentService;

    public RegisterAgentCommandHandler(IAgentService agentService)
    {
        _agentService = agentService;
    }

    public async Task<RegisterAgentResponse> Handle(IReceiveContext<RegisterAgentCommand> context, CancellationToken cancellationToken)
    {
        var result = await _agentService.RegisterAgentAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new RegisterAgentResponse { Data = result };
    }
}
