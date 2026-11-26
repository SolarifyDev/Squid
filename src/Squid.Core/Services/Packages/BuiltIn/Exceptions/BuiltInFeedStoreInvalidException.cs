namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

public sealed class BuiltInFeedStoreInvalidException(int feedId, string value) : InvalidOperationException($"The built-in feed {feedId} names package store '{value}', which is not a store id; set its StoreId property to a store id, or clear it to use the default store")
{
    public int FeedId { get; } = feedId;
}
