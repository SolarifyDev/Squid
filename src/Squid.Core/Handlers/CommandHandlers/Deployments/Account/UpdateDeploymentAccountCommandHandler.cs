using Squid.Core.Services.Deployments.Account;
using Squid.Message.Commands.Deployments.Account;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Account;

public class UpdateDeploymentAccountCommandHandler : ICommandHandler<UpdateDeploymentAccountCommand, UpdateDeploymentAccountResponse>
{
    private readonly IDeploymentAccountService _service;

    public UpdateDeploymentAccountCommandHandler(IDeploymentAccountService service)
    {
        _service = service;
    }

    public async Task<UpdateDeploymentAccountResponse> Handle(IReceiveContext<UpdateDeploymentAccountCommand> context, CancellationToken cancellationToken)
    {
        var result = await _service.UpdateAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new UpdateDeploymentAccountResponse
        {
            Data = result
        };
    }
}
