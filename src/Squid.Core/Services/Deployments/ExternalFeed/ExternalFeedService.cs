using Squid.Message.Commands.Deployments.ExternalFeed;
using Squid.Message.Events.Deployments.ExternalFeed;
using Squid.Message.Models.Deployments.ExternalFeed;
using Squid.Message.Requests.Deployments.ExternalFeed;

namespace Squid.Core.Services.Deployments.ExternalFeed;

public interface IExternalFeedService : IScopedDependency
{
    Task<ExternalFeedCreatedEvent> CreateExternalFeedAsync(CreateExternalFeedCommand command, CancellationToken cancellationToken);

    Task<ExternalFeedUpdatedEvent> UpdateExternalFeedAsync(UpdateExternalFeedCommand command, CancellationToken cancellationToken);

    Task<ExternalFeedDeletedEvent> DeleteExternalFeedsAsync(DeleteExternalFeedsCommand command, CancellationToken cancellationToken);

    Task<GetExternalFeedsResponse> GetExternalFeedsAsync(GetExternalFeedsRequest request, CancellationToken cancellationToken);
}

public class ExternalFeedService : IExternalFeedService
{
    private readonly IMapper _mapper;

    private readonly IExternalFeedDataProvider _externalFeedDataProvider;

    public ExternalFeedService(IMapper mapper, IExternalFeedDataProvider externalFeedDataProvider)
    {
        _mapper = mapper;
        _externalFeedDataProvider = externalFeedDataProvider;
    }

    public async Task<ExternalFeedCreatedEvent> CreateExternalFeedAsync(CreateExternalFeedCommand command, CancellationToken cancellationToken)
    {
        var externalFeed = _mapper.Map<Message.Domain.Deployments.ExternalFeed>(command);

        // externalFeed.Id = Guid.NewGuid(); // int 主键由数据库自增

        await _externalFeedDataProvider.AddExternalFeedAsync(externalFeed, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ExternalFeedCreatedEvent
        {
            Data = _mapper.Map<ExternalFeedDto>(externalFeed)
        };
    }

    public async Task<ExternalFeedUpdatedEvent> UpdateExternalFeedAsync(UpdateExternalFeedCommand command, CancellationToken cancellationToken)
    {
        var feeds = await _externalFeedDataProvider.GetExternalFeedsByIdsAsync(new List<int> { command.Id }, cancellationToken).ConfigureAwait(false);

        var entity = feeds.FirstOrDefault();

        if (entity == null)
        {
            throw new Exception("ExternalFeed not found");
        }

        _mapper.Map(command, entity);

        await _externalFeedDataProvider.UpdateExternalFeedAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ExternalFeedUpdatedEvent
        {
            Data = _mapper.Map<ExternalFeedDto>(entity)
        };
    }

    public async Task<ExternalFeedDeletedEvent> DeleteExternalFeedsAsync(DeleteExternalFeedsCommand command, CancellationToken cancellationToken)
    {
        var feeds = await _externalFeedDataProvider.GetExternalFeedsByIdsAsync(command.Ids, cancellationToken).ConfigureAwait(false);

        await _externalFeedDataProvider.DeleteExternalFeedsAsync(feeds, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ExternalFeedDeletedEvent
        {
            Data = new DeleteExternalFeedsResponseData
            {
                FailIds = command.Ids.Except(feeds.Select(f => f.Id)).ToList()
            }
        };
    }

    public async Task<GetExternalFeedsResponse> GetExternalFeedsAsync(GetExternalFeedsRequest request, CancellationToken cancellationToken)
    {
        var (count, data) = await _externalFeedDataProvider.GetExternalFeedPagingAsync(request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        return new GetExternalFeedsResponse
        {
            Data = new GetExternalFeedsResponseData
            {
                Count = count,
                ExternalFeeds = _mapper.Map<List<ExternalFeedDto>>(data)
            }
        };
    }
}
