using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Message.Constants;

namespace Squid.Core.Services.Packages.BuiltIn;

public sealed class BuiltInFeedService(IRepository repository) : IBuiltInFeedLocator, IBuiltInFeedProvisioner
{
    public const string FeedName = "Built-in feed";

    public const string FeedSlug = "builtin";

    public async Task<ExternalFeed> GetAsync(int spaceId, CancellationToken ct)
    {
        var feed = await repository.QueryNoTracking<ExternalFeed>(f => f.SpaceId == spaceId && f.FeedType == BuiltInFeed.FeedType).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return feed ?? throw new BuiltInFeedNotFoundException(spaceId);
    }

    public async Task EnsureAsync(int spaceId, CancellationToken ct)
    {
        await InsertMissingAsync(spaceId, ct).ConfigureAwait(false);
        await EnsureNoLegacyFeedAsync(spaceId, ct).ConfigureAwait(false);
    }

    public async Task<int> EnsureForAllSpacesAsync(CancellationToken ct)
    {
        var created = await InsertMissingAsync(null, ct).ConfigureAwait(false);

        await EnsureNoLegacyFeedAsync(null, ct).ConfigureAwait(false);

        return created;
    }

    // The unique index on (space_id) for built-in feeds is the arbiter: a feed another caller inserted first makes this a no-op, not an error.
    private Task<int> InsertMissingAsync(int? spaceId, CancellationToken ct)
    {
        return repository.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO external_feed (feed_type, feed_uri, name, slug, space_id, last_modified_date)
            SELECT {BuiltInFeed.FeedType}, '', {FeedName}, {FeedSlug}, s.id, now() FROM space s WHERE {spaceId}::integer IS NULL OR s.id = {spaceId}
            ON CONFLICT (space_id) WHERE feed_type = 'BuiltIn' DO NOTHING
            """, ct);
    }

    // A built-in feed has no URI. One that has was created by hand under the reserved type, and adopting it would hand its package settings to the built-in feed.
    private async Task EnsureNoLegacyFeedAsync(int? spaceId, CancellationToken ct)
    {
        var legacy = await repository.QueryNoTracking<ExternalFeed>(f => f.FeedType == BuiltInFeed.FeedType && f.FeedUri != "" && (spaceId == null || f.SpaceId == spaceId)).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (legacy != null)
            throw new BuiltInFeedConflictException(legacy.Id, legacy.SpaceId, legacy.FeedUri);
    }
}
