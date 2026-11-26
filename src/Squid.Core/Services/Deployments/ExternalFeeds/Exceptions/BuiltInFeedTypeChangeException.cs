namespace Squid.Core.Services.Deployments.ExternalFeeds.Exceptions;

/// <summary>An update would turn the built-in feed into another type, or another feed into the built-in type.</summary>
public sealed class BuiltInFeedTypeChangeException(int feedId, string from, string to) : InvalidOperationException($"The type of feed {feedId} cannot be changed from '{from}' to '{to}': the built-in feed type is fixed")
{
    public int FeedId { get; } = feedId;
}
