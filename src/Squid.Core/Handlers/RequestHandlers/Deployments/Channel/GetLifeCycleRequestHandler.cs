using Squid.Core.Services.Deployments.Channels;
using Squid.Message.Requests.Deployments.Channel;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Channel;

public class GetChannelRequestHandler : IRequestHandler<GetChannelsRequest, GetChannelsResponse>
{
    private readonly IChannelService _channelService;

    public GetChannelRequestHandler(IChannelService channelService)
    {
        _channelService = channelService;
    }

    public async Task<GetChannelsResponse> Handle(IReceiveContext<GetChannelsRequest> context, CancellationToken cancellationToken)
    {
        return await _channelService.GetChannelsAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}