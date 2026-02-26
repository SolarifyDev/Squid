using Squid.Core.Services.Deployments.Account;
using Squid.Message.Commands.Deployments.Account;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Account;

public class CreateDeploymentAccountCommandHandler : ICommandHandler<CreateDeploymentAccountCommand, CreateDeploymentAccountResponse>
{
    private readonly IDeploymentAccountService _service;

    public CreateDeploymentAccountCommandHandler(IDeploymentAccountService service)
    {
        _service = service;
    }

    public async Task<CreateDeploymentAccountResponse> Handle(IReceiveContext<CreateDeploymentAccountCommand> context, CancellationToken cancellationToken)
    {
        var result = await _service.CreateAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new CreateDeploymentAccountResponse
        {
            Data = result
        };
    }
}
