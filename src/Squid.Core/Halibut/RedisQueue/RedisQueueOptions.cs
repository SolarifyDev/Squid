using Squid.Core.Settings.Halibut;

namespace Squid.Core.Halibut.RedisQueue;

/// <summary>The effective cross-pod queue tunables: invalid configured values already replaced by their defaults.</summary>
public sealed record RedisQueueOptions(string KeyPrefix, TimeSpan LivenessWindow, int FailureThreshold, TimeSpan OpenDuration, TimeSpan ProbeInterval, TimeSpan ProbeTimeout, TimeSpan KeyRotation)
{
    /// <summary>How often this replica beats. Init-only so the positional constructor, which tests and the module call, is unchanged.</summary>
    public TimeSpan ReplicaHeartbeat { get; init; } = TimeSpan.FromSeconds(RedisQueueSettings.DefaultReplicaHeartbeatSeconds);

    /// <summary>How long a cross-pod response may take to be written to Redis.</summary>
    public TimeSpan ApplyResponseBudget { get; init; } = TimeSpan.FromSeconds(RedisQueueSettings.DefaultApplyResponseBudgetSeconds);

    /// <summary>Another replica counts as live for this long after its last beat: three beats, so one lost or slow beat does not flip the routing.</summary>
    public TimeSpan ReplicaLiveWindow => ReplicaHeartbeat * 3;

    public static RedisQueueOptions From(HalibutSetting setting)
    {
        var configured = setting?.RedisQueue ?? new RedisQueueSettings();

        return new RedisQueueOptions(
            string.IsNullOrWhiteSpace(configured.KeyPrefix) ? RedisQueueSettings.DefaultKeyPrefix : configured.KeyPrefix.Trim(),
            Seconds(configured.LivenessWindowSeconds, RedisQueueSettings.DefaultLivenessWindowSeconds),
            configured.FailureThreshold > 0 ? configured.FailureThreshold : RedisQueueSettings.DefaultFailureThreshold,
            Seconds(configured.OpenDurationSeconds, RedisQueueSettings.DefaultOpenDurationSeconds),
            Seconds(configured.ProbeIntervalSeconds, RedisQueueSettings.DefaultProbeIntervalSeconds),
            Seconds(configured.ProbeTimeoutSeconds, RedisQueueSettings.DefaultProbeTimeoutSeconds),
            TimeSpan.FromMinutes(configured.KeyRotationMinutes > 0 ? configured.KeyRotationMinutes : RedisQueueSettings.DefaultKeyRotationMinutes))
        {
            ReplicaHeartbeat = Seconds(configured.ReplicaHeartbeatSeconds, RedisQueueSettings.DefaultReplicaHeartbeatSeconds),
            ApplyResponseBudget = Seconds(configured.ApplyResponseBudgetSeconds, RedisQueueSettings.DefaultApplyResponseBudgetSeconds)
        };
    }

    private static TimeSpan Seconds(int configured, int fallback) => TimeSpan.FromSeconds(configured > 0 ? configured : fallback);
}
