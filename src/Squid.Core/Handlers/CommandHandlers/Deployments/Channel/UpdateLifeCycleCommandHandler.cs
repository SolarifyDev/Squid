using Squid.Core.Services.Deployments.Channel;
using Squid.Message.Commands.Deployments.Channel;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Channel;

public class UpdateChannelCommandHandler : ICommandHandler<UpdateChannelCommand, UpdateChannelResponse>
{
    private readonly IChannelService _channelService;

    public UpdateChannelCommandHandler(IChannelService channelService)
    {
        _channelService = channelService;
    }

    public async Task<UpdateChannelResponse> Handle(IReceiveContext<UpdateChannelCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _channelService.UpdateChannelAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new UpdateChannelResponse
        {
            Data = @event.Data
        };
    }
}