using Squid.Core.Services.Spaces;
using Squid.Message.Commands.Spaces;

namespace Squid.Core.Handlers.CommandHandlers.Spaces;

public class CreateSpaceCommandHandler(ISpaceService spaceService) : ICommandHandler<CreateSpaceCommand, CreateSpaceResponse>
{
    public async Task<CreateSpaceResponse> Handle(IReceiveContext<CreateSpaceCommand> context, CancellationToken cancellationToken)
    {
        var space = await spaceService.CreateAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new CreateSpaceResponse { Data = space };
    }
}
