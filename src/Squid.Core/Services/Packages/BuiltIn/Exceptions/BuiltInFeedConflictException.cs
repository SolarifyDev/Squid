namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

/// <summary>A feed of the reserved built-in type exists that is not a built-in feed, so it is not adopted as the space's own; its owner has to retype or delete it.</summary>
public sealed class BuiltInFeedConflictException(int feedId, int spaceId, string feedUri) : InvalidOperationException($"Built-in feed setup cannot continue: feed {feedId} (space {spaceId}) has the type BuiltIn and the URI \"{feedUri}\". A feed of type BuiltIn must have no URI, so it cannot be adopted as the space built-in feed. Change the type of this feed (or delete it) and start Squid again.")
{
    public int FeedId { get; } = feedId;
}
