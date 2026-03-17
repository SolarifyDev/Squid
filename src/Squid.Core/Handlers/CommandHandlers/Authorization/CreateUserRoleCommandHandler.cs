using Squid.Core.Services.Authorization;
using Squid.Message.Commands.Authorization;

namespace Squid.Core.Handlers.CommandHandlers.Authorization;

public class CreateUserRoleCommandHandler(IUserRoleService userRoleService) : ICommandHandler<CreateUserRoleCommand, CreateUserRoleResponse>
{
    public async Task<CreateUserRoleResponse> Handle(IReceiveContext<CreateUserRoleCommand> context, CancellationToken cancellationToken)
    {
        var role = await userRoleService.CreateAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new CreateUserRoleResponse { Data = role };
    }
}
