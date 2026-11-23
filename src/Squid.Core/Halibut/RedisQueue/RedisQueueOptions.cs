using Squid.Core.Settings.Halibut;

namespace Squid.Core.Halibut.RedisQueue;

/// <summary>The effective cross-pod queue tunables: invalid configured values already replaced by their defaults.</summary>
public sealed record RedisQueueOptions(string KeyPrefix, TimeSpan LivenessWindow, int FailureThreshold, TimeSpan OpenDuration, TimeSpan ProbeInterval, TimeSpan ProbeTimeout, TimeSpan KeyRotation)
{
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
            TimeSpan.FromMinutes(configured.KeyRotationMinutes > 0 ? configured.KeyRotationMinutes : RedisQueueSettings.DefaultKeyRotationMinutes));
    }

    private static TimeSpan Seconds(int configured, int fallback) => TimeSpan.FromSeconds(configured > 0 ? configured : fallback);
}
