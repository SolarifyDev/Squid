using Squid.Core.Services.Deployments.ExternalFeeds.PackageNotes;
using Squid.Message.Requests.Deployments.ExternalFeed;

namespace Squid.Core.Services.Deployments.ExternalFeeds;

public interface IExternalFeedPackageNotesService : IScopedDependency
{
    Task<GetPackageNotesResponseData> GetNotesAsync(List<PackageNotesQuery> queries, CancellationToken ct);
}

public class ExternalFeedPackageNotesService(
    IExternalFeedDataProvider dataProvider,
    IEnumerable<IPackageNotesStrategy> strategies) : IExternalFeedPackageNotesService
{
    public async Task<GetPackageNotesResponseData> GetNotesAsync(List<PackageNotesQuery> queries, CancellationToken ct)
    {
        if (queries == null || queries.Count == 0)
            return new GetPackageNotesResponseData();

        var feedGroups = queries.GroupBy(q => q.FeedId);
        var tasks = feedGroups.Select(group => ProcessFeedGroupAsync(group.Key, group.ToList(), ct));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return new GetPackageNotesResponseData { Packages = results.SelectMany(r => r).ToList() };
    }

    private async Task<List<PackageNotesItem>> ProcessFeedGroupAsync(int feedId, List<PackageNotesQuery> queries, CancellationToken ct)
    {
        var feed = await dataProvider.GetFeedByIdAsync(feedId, ct).ConfigureAwait(false);

        if (feed == null)
            return queries.Select(q => ToFailureItem(q, "Feed not found")).ToList();

        var strategy = strategies.FirstOrDefault(s => s.CanHandle(feed.FeedType));

        if (strategy == null)
            return queries.Select(q => ToFailureItem(q, $"Unsupported feed type: {feed.FeedType}")).ToList();

        var items = new List<PackageNotesItem>();

        foreach (var query in queries)
        {
            try
            {
                var result = await strategy.GetNotesAsync(feed, query.PackageId, query.Version, ct).ConfigureAwait(false);
                items.Add(ToItem(query, result));
            }
            catch (Exception ex)
            {
                items.Add(ToFailureItem(query, ex.Message));
            }
        }

        return items;
    }

    private static PackageNotesItem ToItem(PackageNotesQuery query, PackageNotesResult result) => new()
    {
        FeedId = query.FeedId,
        PackageId = query.PackageId,
        Version = query.Version,
        Succeeded = result.Succeeded,
        Notes = result.Notes,
        FailureReason = result.FailureReason,
        Published = result.Published
    };

    private static PackageNotesItem ToFailureItem(PackageNotesQuery query, string reason) => new()
    {
        FeedId = query.FeedId,
        PackageId = query.PackageId,
        Version = query.Version,
        Succeeded = false,
        FailureReason = reason
    };
}
