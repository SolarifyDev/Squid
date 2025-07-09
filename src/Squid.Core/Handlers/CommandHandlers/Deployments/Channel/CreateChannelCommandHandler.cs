using Squid.Core.Services.Deployments.Channel;
using Squid.Message.Commands.Deployments.Channel;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Channel;

public class CreateChannelCommandHandler : ICommandHandler<CreateChannelCommand, CreateChannelResponse>
{
    private readonly IChannelService _channelService;

    public CreateChannelCommandHandler(IChannelService channelService)
    {
        _channelService = channelService;
    }

    public async Task<CreateChannelResponse> Handle(IReceiveContext<CreateChannelCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _channelService.CreateChannelAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new CreateChannelResponse
        {
            Data = @event.Data
        };
    }
}