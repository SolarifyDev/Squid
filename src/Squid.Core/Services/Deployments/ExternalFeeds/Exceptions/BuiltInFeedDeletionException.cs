namespace Squid.Core.Services.Deployments.ExternalFeeds.Exceptions;

public sealed class BuiltInFeedDeletionException(int feedId) : InvalidOperationException($"Feed {feedId} is the space's built-in package feed and cannot be deleted")
{
    public int FeedId { get; } = feedId;
}
