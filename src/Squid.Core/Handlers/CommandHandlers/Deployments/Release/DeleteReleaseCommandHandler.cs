using Squid.Core.Services.Deployments.Release;
using Squid.Message.Commands.Deployments.Release;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Release;

public class DeleteReleaseCommandHandler : ICommandHandler<DeleteReleaseCommand>
{
    private readonly IReleaseService _releaseService;

    public DeleteReleaseCommandHandler(IReleaseService releaseService)
    {
        _releaseService = releaseService;
    }

    public async Task Handle(IReceiveContext<DeleteReleaseCommand> context, CancellationToken cancellationToken)
    {
        await _releaseService.DeleteReleaseAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}