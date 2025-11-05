using Squid.Core.Services.Deployments.Release;
using Squid.Message.Commands.Deployments.Release;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Release;

public class UpdateReleaseCommandHandler : ICommandHandler<UpdateReleaseCommand, UpdateReleaseResponse>
{
    private readonly IReleaseService _releaseService;

    public UpdateReleaseCommandHandler(IReleaseService releaseService)
    {
        _releaseService = releaseService;
    }

    public async Task<UpdateReleaseResponse> Handle(IReceiveContext<UpdateReleaseCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _releaseService.UpdateReleaseAsync(context.Message, cancellationToken).ConfigureAwait(false);
        
        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);
        
        return new UpdateReleaseResponse
        {
            Data = @event.Release
        };
    }
}