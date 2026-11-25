namespace Squid.Message.Constants;

/// <summary>The built-in package feed: one per space, holding the packages people push to Squid directly.</summary>
public static class BuiltInFeed
{
    /// <summary>The <c>FeedType</c> of the built-in feed. Matched exactly (ignoring case) wherever a feed is told apart by type.</summary>
    public const string FeedType = "BuiltIn";

    /// <summary>Feed property naming the package store new pushes go to. Empty or absent means the default store.</summary>
    public const string StoreIdProperty = "StoreId";

    /// <summary>The largest package a push accepts: 2 GiB.</summary>
    public const long MaxPackageSizeBytes = 2L * 1024 * 1024 * 1024;

    public static bool IsBuiltIn(string feedType) => string.Equals(feedType, FeedType, StringComparison.OrdinalIgnoreCase);
}
