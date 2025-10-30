using Squid.Core.Services.Deployments.Release;
using Squid.Message.Commands.Deployments.Release;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Release;

public class UpdateReleaseVariableCommandHandler : ICommandHandler<UpdateReleaseVariableCommand>
{
    private readonly IReleaseService _releaseService;

    public UpdateReleaseVariableCommandHandler(IReleaseService releaseService)
    {
        _releaseService = releaseService;
    }

    public async Task Handle(IReceiveContext<UpdateReleaseVariableCommand> context, CancellationToken cancellationToken)
    {
        await _releaseService.UpdateReleaseVariableAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}