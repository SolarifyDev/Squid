using Squid.Core.Settings.Halibut;

namespace Squid.Core.Halibut.DataStreamStorage;

/// <summary>The effective storage tunables: invalid configured values already replaced by their defaults.</summary>
public sealed record DataStreamStorageOptions(int ChunkBytes, TimeSpan OrphanTtl)
{
    public static DataStreamStorageOptions From(HalibutSetting setting)
    {
        var configured = setting?.DataStreamStorage ?? new DataStreamStorageSettings();
        var ttlMinutes = configured.OrphanTtlMinutes > 0 ? configured.OrphanTtlMinutes : DataStreamStorageSettings.DefaultOrphanTtlMinutes;

        return new DataStreamStorageOptions(ChunkBytesOrDefault(configured.ChunkBytes), TimeSpan.FromMinutes(ttlMinutes));
    }

    private static int ChunkBytesOrDefault(int configured)
        => configured <= 0 ? DataStreamStorageSettings.DefaultChunkBytes : Math.Min(configured, DataStreamStorageSettings.MaxChunkBytes);
}
