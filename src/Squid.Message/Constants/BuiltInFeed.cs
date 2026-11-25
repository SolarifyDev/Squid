namespace Squid.Message.Constants;

/// <summary>The built-in package feed: one per space, holding the packages people push to Squid directly.</summary>
public static class BuiltInFeed
{
    /// <summary>The <c>FeedType</c> of the built-in feed. One case rule everywhere: the index, the queries, <see cref="IsBuiltIn"/>, the strategies and the package source all match this exactly (ordinal).</summary>
    public const string FeedType = "BuiltIn";

    /// <summary>Feed property naming the package store new pushes go to. Empty or absent means the default store.</summary>
    public const string StoreIdProperty = "StoreId";

    /// <summary>The largest package a push accepts: 2 GiB.</summary>
    public const long MaxPackageSizeBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>The most entries an archive may hold; the walk at push time stops at the first one over.</summary>
    public const int MaxArchiveEntries = 100_000;

    /// <summary>The most an archive may hold once unpacked: 16 GiB.</summary>
    public const long MaxUncompressedBytes = 16L * 1024 * 1024 * 1024;

    /// <summary>The largest unpacked-to-packed ratio an archive may have, checked once <see cref="CompressionRatioFloorBytes"/> have been unpacked so small, highly compressible archives are not mistaken for bombs.</summary>
    public const int MaxCompressionRatio = 250;

    public const long CompressionRatioFloorBytes = 256L * 1024 * 1024;

    /// <summary>True only for exactly <see cref="FeedType"/>.</summary>
    public static bool IsBuiltIn(string feedType) => string.Equals(feedType, FeedType, StringComparison.Ordinal);

    /// <summary>True for the built-in type in any casing: such a type can neither be created nor given to a feed through the feed commands.</summary>
    public static bool IsReservedType(string feedType) => string.Equals(feedType, FeedType, StringComparison.OrdinalIgnoreCase);
}
