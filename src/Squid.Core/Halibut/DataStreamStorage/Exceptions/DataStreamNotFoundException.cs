namespace Squid.Core.Halibut.DataStreamStorage.Exceptions;

/// <summary>A request's data stream is not in the shared storage: already collected and deleted, swept as an orphan, or never stored.</summary>
public sealed class DataStreamNotFoundException(Guid streamId) : InvalidOperationException($"No stored data found for DataStream with ID: {streamId}")
{
    public Guid StreamId { get; } = streamId;
}
