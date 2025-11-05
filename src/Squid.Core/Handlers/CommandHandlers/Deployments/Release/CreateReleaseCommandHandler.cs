using Squid.Core.Services.Deployments.Release;
using Squid.Message.Commands.Deployments.Release;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Release;

public class CreateReleaseCommandHandler : ICommandHandler<CreateReleaseCommand, CreateReleaseResponse>
{
    private readonly IReleaseService _releaseService;

    public CreateReleaseCommandHandler(IReleaseService releaseService)
    {
        _releaseService = releaseService;
    }

    public async Task<CreateReleaseResponse> Handle(IReceiveContext<CreateReleaseCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _releaseService.CreateReleaseAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new CreateReleaseResponse
        {
            Data = @event.Release
        };
    }
}