using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Message.Constants;

namespace Squid.Core.Services.Packages.BuiltIn;

public interface IBuiltInFeedLocator : IScopedDependency
{
    /// <summary>The space's built-in feed. Throws <c>BuiltInFeedNotFoundException</c> when the space has none.</summary>
    Task<ExternalFeed> GetAsync(int spaceId, CancellationToken ct);
}

public interface IBuiltInFeedProvisioner : IScopedDependency
{
    /// <summary>Gives the space its built-in feed unless it has one. Safe to call again, and safe when two callers race.</summary>
    Task EnsureAsync(int spaceId, CancellationToken ct);

    /// <summary>Does <see cref="EnsureAsync"/> for every space and returns how many feeds it created.</summary>
    Task<int> EnsureForAllSpacesAsync(CancellationToken ct);
}

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
    }

    public Task<int> EnsureForAllSpacesAsync(CancellationToken ct) => InsertMissingAsync(null, ct);

    // The unique index on (space_id) for built-in feeds is the arbiter: a feed another caller inserted first makes this a no-op, not an error.
    private Task<int> InsertMissingAsync(int? spaceId, CancellationToken ct)
    {
        return repository.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO external_feed (feed_type, feed_uri, name, slug, space_id, last_modified_date)
            SELECT {BuiltInFeed.FeedType}, '', {FeedName}, {FeedSlug}, s.id, now() FROM space s WHERE {spaceId}::integer IS NULL OR s.id = {spaceId}
            ON CONFLICT (space_id) WHERE feed_type = 'BuiltIn' DO NOTHING
            """, ct);
    }
}
