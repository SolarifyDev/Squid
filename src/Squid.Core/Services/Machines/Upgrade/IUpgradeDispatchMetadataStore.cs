using System.Text.Json;
using Squid.Core.Services.Caching.Redis;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// H4 — companion store for the Redis upgrade-dispatch lock. Holds a small
/// JSON metadata blob (when the dispatch started, what version was targeted)
/// at <c>squid:upgrade:machine:{id}:meta</c>, written when the lock is
/// acquired and deleted when it's released.
///
/// <para><b>Why this exists</b>: pre-H4, when an operator double-clicked
/// "Upgrade", the second click hit the lock-contention path and got
/// <c>"Machine 'X' is currently being upgraded by another request. Wait for
/// it to complete (typically under 2 minutes) and retry."</c>. The "typically
/// under 2 minutes" was a hardcoded guess — the operator had no idea when
/// the original dispatch actually started or what version it was targeting.
/// H4 attaches metadata so the contention message can say <c>"In progress
/// since 12:34:56 (started ~45s ago), targeting 1.8.0. Expected completion
/// within 7 minutes from start."</c>.</para>
///
/// <para><b>Lifecycle</b>: same TTL as the RedLockNet lock so the metadata
/// is automatically cleaned up if the dispatcher crashes — no separate
/// expiry tracking needed. Best-effort writes: a failed metadata write logs
/// + does NOT fail the dispatch (the lock is still held and the upgrade
/// proceeds; the contention UX just falls back to the old hardcoded
/// message).</para>
/// </summary>
public interface IUpgradeDispatchMetadataStore : IScopedDependency
{
    /// <summary>Write metadata for the given machine's in-flight dispatch.
    /// TTL matches the lock's expiry — best-effort: errors are logged but
    /// do not propagate (the dispatch should not fail because of a
    /// metadata-write hiccup).</summary>
    Task WriteAsync(int machineId, UpgradeDispatchMetadata metadata, TimeSpan ttl, CancellationToken ct);

    /// <summary>Read the metadata for a machine if a dispatch is in flight.
    /// Returns <c>null</c> when nothing is recorded (or on Redis error).</summary>
    Task<UpgradeDispatchMetadata> ReadAsync(int machineId, CancellationToken ct);

    /// <summary>Best-effort delete on successful dispatch release. If the
    /// delete fails, the TTL catches it.</summary>
    Task DeleteAsync(int machineId, CancellationToken ct);
}

/// <summary>
/// Wire-stable JSON shape — pin by unit test. Property names are
/// camelCase to match the rest of the upgrade API. New fields stay
/// nullable to preserve backward-compat with older blobs.
/// </summary>
public sealed class UpgradeDispatchMetadata
{
    public DateTimeOffset DispatchedAt { get; init; }
    public string TargetVersion { get; init; }
    public string CurrentVersion { get; init; }
}

public sealed class UpgradeDispatchMetadataStore : IUpgradeDispatchMetadataStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IRedisSafeRunner _redis;

    public UpgradeDispatchMetadataStore(IRedisSafeRunner redis)
    {
        _redis = redis;
    }

    /// <summary>Companion key — co-located with the lock at
    /// <c>squid:upgrade:machine:{id}</c>. Suffix keeps them grouped under the
    /// same key-space prefix for easier ops queries
    /// (<c>SCAN squid:upgrade:machine:*</c>).</summary>
    internal static string BuildMetadataKey(int machineId)
        => $"{UpgradeDispatchLockReconciler.BuildLockKey(machineId)}:meta";

    public async Task WriteAsync(int machineId, UpgradeDispatchMetadata metadata, TimeSpan ttl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var key = BuildMetadataKey(machineId);
        var json = JsonSerializer.Serialize(metadata, SerializerOptions);

        await _redis.ExecuteAsync(async multiplexer =>
        {
            await multiplexer.GetDatabase()
                .StringSetAsync(key, json, expiry: ttl)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task<UpgradeDispatchMetadata> ReadAsync(int machineId, CancellationToken ct)
    {
        var key = BuildMetadataKey(machineId);

        var json = await _redis.ExecuteAsync<string>(async multiplexer =>
        {
            var value = await multiplexer.GetDatabase().StringGetAsync(key).ConfigureAwait(false);
            return value.HasValue ? value.ToString() : null;
        }).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<UpgradeDispatchMetadata>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            // Corrupted JSON shouldn't crash the contention path. Log + return
            // null so the caller falls back to the old hardcoded message.
            Log.Warning(ex,
                "Failed to deserialise upgrade dispatch metadata for machine {MachineId} (key {Key}). " +
                "Contention message falls back to the generic 'in progress' shape.",
                machineId, key);
            return null;
        }
    }

    public async Task DeleteAsync(int machineId, CancellationToken ct)
    {
        var key = BuildMetadataKey(machineId);

        await _redis.ExecuteAsync(async multiplexer =>
        {
            await multiplexer.GetDatabase().KeyDeleteAsync(key).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}
