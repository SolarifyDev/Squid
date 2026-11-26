using Squid.Core.Services.Deployments.ExternalFeeds.Exceptions;
using Squid.Message.Commands.Deployments.ExternalFeed;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Events.Deployments.ExternalFeed;
using Squid.Message.Models.Deployments.ExternalFeed;
using Squid.Message.Requests.Deployments.ExternalFeed;

namespace Squid.Core.Services.Deployments.ExternalFeeds;

public interface IExternalFeedService : IScopedDependency
{
    Task<ExternalFeedCreatedEvent> CreateExternalFeedAsync(CreateExternalFeedCommand command, CancellationToken cancellationToken);

    Task<ExternalFeedUpdatedEvent> UpdateExternalFeedAsync(UpdateExternalFeedCommand command, CancellationToken cancellationToken);

    Task<ExternalFeedDeletedEvent> DeleteExternalFeedsAsync(DeleteExternalFeedsCommand command, CancellationToken cancellationToken);

    Task<GetExternalFeedsResponse> GetExternalFeedsAsync(GetExternalFeedsRequest request, CancellationToken cancellationToken);

    /// <summary>Username and whether a password is stored, per feed; passwords are never read.</summary>
    Task<IReadOnlyList<ExternalFeedCredentialSlot>> GetCredentialSlotsAsync(IReadOnlyCollection<int> feedIds, CancellationToken cancellationToken);

    /// <summary>
    /// Fills the feed's credentials only while it has no password and its username is empty or already <paramref name="username"/>
    /// (compare-and-set): credentials set by anyone in the meantime are never overwritten. Returns <see cref="SecretSlotState.Filled"/>,
    /// or why nothing was written.
    /// </summary>
    Task<SecretSlotState> FillMissingCredentialsAsync(int feedId, string username, string password, CancellationToken cancellationToken);
}

public class ExternalFeedService(IMapper mapper, IExternalFeedDataProvider externalFeedDataProvider) : IExternalFeedService
{
    public async Task<ExternalFeedCreatedEvent> CreateExternalFeedAsync(CreateExternalFeedCommand command, CancellationToken cancellationToken)
    {
        if (BuiltInFeed.IsReservedType(command.FeedType))
            throw new BuiltInFeedCreationException();

        var externalFeed = mapper.Map<Persistence.Entities.Deployments.ExternalFeed>(command);

        await externalFeedDataProvider.AddExternalFeedAsync(externalFeed, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ExternalFeedCreatedEvent
        {
            Data = mapper.Map<ExternalFeedDto>(externalFeed)
        };
    }

    public async Task<ExternalFeedUpdatedEvent> UpdateExternalFeedAsync(UpdateExternalFeedCommand command, CancellationToken cancellationToken)
    {
        var feeds = await externalFeedDataProvider.GetExternalFeedsByIdsAsync(new List<int> { command.Id }, cancellationToken).ConfigureAwait(false);

        var entity = feeds.FirstOrDefault();

        if (entity == null)
        {
            throw new Exception("ExternalFeed not found");
        }

        EnsureBuiltInTypeUnchanged(entity, command.FeedType);

        // Keep existing password when update request doesn't provide a new one.
        var existingPassword = entity.Password;
        
        mapper.Map(command, entity);
        
        if (string.IsNullOrWhiteSpace(command.PasswordNewValue)) entity.Password = existingPassword;

        // The built-in feed has no URI and the column cannot be null.
        if (BuiltInFeed.IsBuiltIn(entity.FeedType)) entity.FeedUri ??= string.Empty;

        await externalFeedDataProvider.UpdateExternalFeedAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ExternalFeedUpdatedEvent
        {
            Data = mapper.Map<ExternalFeedDto>(entity)
        };
    }

    public async Task<ExternalFeedDeletedEvent> DeleteExternalFeedsAsync(DeleteExternalFeedsCommand command, CancellationToken cancellationToken)
    {
        var feeds = await externalFeedDataProvider.GetExternalFeedsByIdsAsync(command.Ids, cancellationToken).ConfigureAwait(false);

        EnsureNoBuiltInFeed(feeds);

        await externalFeedDataProvider.DeleteExternalFeedsAsync(feeds, cancellationToken: cancellationToken).ConfigureAwait(false);

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
        var (count, data) = await externalFeedDataProvider.GetExternalFeedPagingAsync(request.SpaceId, request.PageIndex, request.PageSize, cancellationToken).ConfigureAwait(false);

        return new GetExternalFeedsResponse
        {
            Data = new GetExternalFeedsResponseData
            {
                Count = count,
                ExternalFeeds = mapper.Map<List<ExternalFeedDto>>(data)
            }
        };
    }

    public async Task<IReadOnlyList<ExternalFeedCredentialSlot>> GetCredentialSlotsAsync(IReadOnlyCollection<int> feedIds, CancellationToken cancellationToken)
        => await externalFeedDataProvider.GetCredentialSlotsAsync(feedIds, cancellationToken).ConfigureAwait(false);

    public async Task<SecretSlotState> FillMissingCredentialsAsync(int feedId, string username, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("A fill needs a password: an empty source password is never written.", nameof(password));

        var slots = await externalFeedDataProvider.GetCredentialSlotsAsync([feedId], cancellationToken).ConfigureAwait(false);
        var state = ExternalFeedCredentialSlots.StateOf(slots.FirstOrDefault(), username);

        if (state != SecretSlotState.Empty) return state;

        var written = await externalFeedDataProvider.SetCredentialsIfEmptyAsync(feedId, username, password, cancellationToken).ConfigureAwait(false);

        return written == 0 ? SecretSlotState.AlreadySet : SecretSlotState.Filled;
    }

    private static void EnsureBuiltInTypeUnchanged(Persistence.Entities.Deployments.ExternalFeed entity, string newFeedType)
    {
        var keepsBuiltIn = BuiltInFeed.IsBuiltIn(entity.FeedType) && BuiltInFeed.IsBuiltIn(newFeedType);

        // Any casing of the reserved type counts as the built-in type here, so "builtin" cannot slip past as a different one.
        if (!keepsBuiltIn && (BuiltInFeed.IsReservedType(entity.FeedType) || BuiltInFeed.IsReservedType(newFeedType)))
            throw new BuiltInFeedTypeChangeException(entity.Id, entity.FeedType, newFeedType);
    }

    private static void EnsureNoBuiltInFeed(List<Persistence.Entities.Deployments.ExternalFeed> feeds)
    {
        var builtIn = feeds.FirstOrDefault(f => BuiltInFeed.IsBuiltIn(f.FeedType));

        if (builtIn != null)
            throw new BuiltInFeedDeletionException(builtIn.Id);
    }
}
