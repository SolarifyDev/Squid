using Squid.Core.Services.Deployments.ExternalFeed;
using Squid.Message.Commands.Deployments.ExternalFeed;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.ExternalFeed;

public class CreateExternalFeedCommandHandler : ICommandHandler<CreateExternalFeedCommand, CreateExternalFeedResponse>
{
    private readonly IExternalFeedService _externalFeedService;

    public CreateExternalFeedCommandHandler(IExternalFeedService externalFeedService)
    {
        _externalFeedService = externalFeedService;
    }

    public async Task<CreateExternalFeedResponse> Handle(IReceiveContext<CreateExternalFeedCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _externalFeedService.CreateExternalFeedAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new CreateExternalFeedResponse
        {
            Data = new CreateExternalFeedResponseData
            {
                ExternalFeed = @event.Data
            }
        };
    }
}
