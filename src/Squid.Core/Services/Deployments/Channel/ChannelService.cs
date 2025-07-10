using Squid.Message.Commands.Deployments.Channel;
using Squid.Message.Events.Deployments.Channel;
using Squid.Message.Models.Deployments.Channel;
using Squid.Message.Requests.Deployments.Channel;

namespace Squid.Core.Services.Deployments.Channel;

public interface IChannelService : IScopedDependency
{
    Task<CreateChannelEvent> CreateChannelAsync(CreateChannelCommand command, CancellationToken cancellationToken);
    
    Task<UpdateChannelEvent> UpdateChannelAsync(UpdateChannelCommand command, CancellationToken cancellationToken);
    
    Task<DeleteChannelEvent> DeleteChannelsAsync(DeleteChannelsCommand command, CancellationToken cancellationToken);
    
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
    
    public async Task<CreateChannelEvent> CreateChannelAsync(CreateChannelCommand command, CancellationToken cancellationToken)
    {
        var channel = _mapper.Map<Message.Domain.Deployments.Channel>(command.Channel);
        
        await _channelDataProvider.AddChannelAsync(channel, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new CreateChannelEvent()
        {
            Data = _mapper.Map<ChannelDto>(channel)
        };
    }

    public async Task<UpdateChannelEvent> UpdateChannelAsync(UpdateChannelCommand command, CancellationToken cancellationToken)
    {
        var channel = _mapper.Map<Message.Domain.Deployments.Channel>(command.Channel);
        
        await _channelDataProvider.UpdateChannelAsync(channel, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new UpdateChannelEvent()
        {
            Data = _mapper.Map<ChannelDto>(channel)
        };
    }

    public async Task<DeleteChannelEvent> DeleteChannelsAsync(DeleteChannelsCommand command, CancellationToken cancellationToken)
    {
        var channels = await _channelDataProvider.GetChannelsAsync(command.Ids, cancellationToken).ConfigureAwait(false);
        
        await _channelDataProvider.DeleteChannelsAsync(channels, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new DeleteChannelEvent()
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

        return new GetChannelsResponse()
        {
            Data = new GetChannelsResponseData()
            {
                Count = count, Channels = _mapper.Map<List<ChannelDto>>(data)
            }
        };
    }
}