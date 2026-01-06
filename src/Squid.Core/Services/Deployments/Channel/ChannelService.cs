using Squid.Message.Commands.Deployments.Channel;
using Squid.Message.Events.Deployments.Channel;
using Squid.Message.Models.Deployments.Channel;
using Squid.Message.Requests.Deployments.Channel;

namespace Squid.Core.Services.Deployments.Channel;

public interface IChannelService : IScopedDependency
{
    Task<ChannelCreatedEvent> CreateChannelAsync(CreateChannelCommand command, CancellationToken cancellationToken);
    
    Task<ChannelUpdatedEvent> UpdateChannelAsync(UpdateChannelCommand command, CancellationToken cancellationToken);
    
    Task<ChannelDeletedEvent> DeleteChannelsAsync(DeleteChannelsCommand command, CancellationToken cancellationToken);
    
    Task<GetChannelsResponse> GetChannelsAsync(GetChannelsRequest request, CancellationToken cancellationToken);
}

public class ChannelService : IChannelService
{
    private readonly IMapper _mapper;
    private readonly IChannelDataProvider _channelDataProvider;

    public ChannelService(IMapper mapper, IChannelDataProvider channelDataProvider)
    {
        _mapper = mapper;
        _channelDataProvider = channelDataProvider;
    }
    
    public async Task<ChannelCreatedEvent> CreateChannelAsync(CreateChannelCommand command, CancellationToken cancellationToken)
    {
        var channel = _mapper.Map<Persistence.Data.Domain.Deployments.Channel>(command.Channel);
        
        await _channelDataProvider.AddChannelAsync(channel, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ChannelCreatedEvent
        {
            Data = _mapper.Map<ChannelDto>(channel)
        };
    }

    public async Task<ChannelUpdatedEvent> UpdateChannelAsync(UpdateChannelCommand command, CancellationToken cancellationToken)
    {
        var channel = _mapper.Map<Persistence.Data.Domain.Deployments.Channel>(command.Channel);
        
        await _channelDataProvider.UpdateChannelAsync(channel, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ChannelUpdatedEvent
        {
            Data = _mapper.Map<ChannelDto>(channel)
        };
    }

    public async Task<ChannelDeletedEvent> DeleteChannelsAsync(DeleteChannelsCommand command, CancellationToken cancellationToken)
    {
        var channels = await _channelDataProvider.GetChannelsAsync(command.Ids, cancellationToken).ConfigureAwait(false);
        
        await _channelDataProvider.DeleteChannelsAsync(channels, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ChannelDeletedEvent
        {
            Data = new DeleteChannelsResponseData
            {
                FailIds = command.Ids.Except(channels.Select(c => c.Id)).ToList()
            }
        };
    }

    public async Task<GetChannelsResponse> GetChannelsAsync(GetChannelsRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await _channelDataProvider.GetChannelPagingAsync(request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        return new GetChannelsResponse
        {
            Data = new GetChannelsResponseData
            {
                Count = count, Channels = _mapper.Map<List<ChannelDto>>(data)
            }
        };
    }
}
