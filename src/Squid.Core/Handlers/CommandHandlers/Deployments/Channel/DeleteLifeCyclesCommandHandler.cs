using Squid.Core.Services.Deployments.Channel;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Message.Commands.Deployments.Channel;
using Squid.Message.Commands.Deployments.LifeCycle;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Channel;

public class DeleteChannelsCommandHandler : ICommandHandler<DeleteChannelsCommand, DeleteChannelsResponse>
{
    private readonly IChannelService _channelService;

    public DeleteChannelsCommandHandler(IChannelService channelService)
    {
        _channelService = channelService;
    }

    public async Task<DeleteChannelsResponse> Handle(IReceiveContext<DeleteChannelsCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _channelService.DeleteChannelsAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new DeleteChannelsResponse
        {
            Data = @event.Data
        };
    }
}