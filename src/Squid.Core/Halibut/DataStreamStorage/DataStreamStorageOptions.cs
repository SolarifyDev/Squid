using Squid.Core.Settings.Halibut;

namespace Squid.Core.Halibut.DataStreamStorage;

/// <summary>The effective storage tunables: invalid configured values already replaced by their defaults.</summary>
public sealed record DataStreamStorageOptions(int ChunkBytes, TimeSpan OrphanTtl)
{
    /// <summary>The least a stored stream may be kept: Halibut keeps a response 20 minutes and a request for its pickup timeout, and the agent then reads the stream.</summary>
    public static readonly TimeSpan MinimumOrphanTtl = TimeSpan.FromMinutes(20);

    /// <summary>What an orphan TTL must exceed the request pickup timeout by: the time the agent takes to read a large stream after collecting the request.</summary>
    public static readonly TimeSpan OrphanTtlMargin = TimeSpan.FromMinutes(10);

    /// <summary>How long one data key seals streams before a new one is made. Init-only so the positional constructor, which tests and the store call, is unchanged.</summary>
    public TimeSpan KeyRotation { get; init; } = TimeSpan.FromMinutes(DataStreamStorageSettings.DefaultKeyRotationMinutes);

    /// <summary>The shortest orphan TTL that cannot remove a stream a live request still needs, given the request pickup timeout in force.</summary>
    public static TimeSpan SafeOrphanTtl(TimeSpan pollingQueueTimeout) => TimeSpan.FromTicks(Math.Max(MinimumOrphanTtl.Ticks, (pollingQueueTimeout + OrphanTtlMargin).Ticks));

    /// <summary>The configured TTL, raised to <see cref="SafeOrphanTtl"/> when it is shorter: a sweep must never delete the stream of a request that is still waiting.</summary>
    public DataStreamStorageOptions WithSafeOrphanTtl(TimeSpan pollingQueueTimeout) => this with { OrphanTtl = TimeSpan.FromTicks(Math.Max(OrphanTtl.Ticks, SafeOrphanTtl(pollingQueueTimeout).Ticks)) };

    public static DataStreamStorageOptions From(HalibutSetting setting)
    {
        var configured = setting?.DataStreamStorage ?? new DataStreamStorageSettings();
        var ttlMinutes = configured.OrphanTtlMinutes > 0 ? configured.OrphanTtlMinutes : DataStreamStorageSettings.DefaultOrphanTtlMinutes;

        return new DataStreamStorageOptions(ChunkBytesOrDefault(configured.ChunkBytes), TimeSpan.FromMinutes(ttlMinutes))
        {
            KeyRotation = TimeSpan.FromMinutes(configured.KeyRotationMinutes > 0 ? configured.KeyRotationMinutes : DataStreamStorageSettings.DefaultKeyRotationMinutes)
        };
    }

    private static int ChunkBytesOrDefault(int configured)
        => configured <= 0 ? DataStreamStorageSettings.DefaultChunkBytes : Math.Min(configured, DataStreamStorageSettings.MaxChunkBytes);
}
