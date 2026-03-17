using Squid.Core.Services.Spaces;
using Squid.Message.Commands.Spaces;

namespace Squid.Core.Handlers.CommandHandlers.Spaces;

public class DeleteSpaceCommandHandler(ISpaceService spaceService) : ICommandHandler<DeleteSpaceCommand, DeleteSpaceResponse>
{
    public async Task<DeleteSpaceResponse> Handle(IReceiveContext<DeleteSpaceCommand> context, CancellationToken cancellationToken)
    {
        await spaceService.DeleteAsync(context.Message.Id, cancellationToken).ConfigureAwait(false);

        return new DeleteSpaceResponse();
    }
}
