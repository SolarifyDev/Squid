using Squid.Core.Services.Deployments.Account;
using Squid.Message.Commands.Deployments.Account;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Account;

public class DeleteDeploymentAccountsCommandHandler : ICommandHandler<DeleteDeploymentAccountsCommand, DeleteDeploymentAccountsResponse>
{
    private readonly IDeploymentAccountService _service;

    public DeleteDeploymentAccountsCommandHandler(IDeploymentAccountService service)
    {
        _service = service;
    }

    public async Task<DeleteDeploymentAccountsResponse> Handle(IReceiveContext<DeleteDeploymentAccountsCommand> context, CancellationToken cancellationToken)
    {
        var result = await _service.DeleteAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new DeleteDeploymentAccountsResponse
        {
            Data = result
        };
    }
}
