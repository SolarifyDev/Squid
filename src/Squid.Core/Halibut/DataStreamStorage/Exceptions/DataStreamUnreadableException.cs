namespace Squid.Core.Halibut.DataStreamStorage.Exceptions;

/// <summary>A stored data stream cannot be read back intact: a chunk is missing or changed, its length differs from the header, or its key cannot be unwrapped.</summary>
public sealed class DataStreamUnreadableException(Guid streamId, string reason, Exception inner = null) : InvalidOperationException($"Stored DataStream {streamId} cannot be read: {reason}", inner)
{
    public Guid StreamId { get; } = streamId;
}
