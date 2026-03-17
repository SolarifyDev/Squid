using Squid.Core.Services.Spaces;
using Squid.Message.Commands.Spaces;

namespace Squid.Core.Handlers.CommandHandlers.Spaces;

public class UpdateSpaceCommandHandler(ISpaceService spaceService) : ICommandHandler<UpdateSpaceCommand, UpdateSpaceResponse>
{
    public async Task<UpdateSpaceResponse> Handle(IReceiveContext<UpdateSpaceCommand> context, CancellationToken cancellationToken)
    {
        var space = await spaceService.UpdateAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new UpdateSpaceResponse { Data = space };
    }
}
