namespace Squid.Core.Services.Packages.Versioning.Exceptions;

public sealed class PackageVersionFeedNotFoundException(int feedId) : InvalidOperationException($"Feed {feedId} was not found")
{
    public int FeedId { get; } = feedId;
}
