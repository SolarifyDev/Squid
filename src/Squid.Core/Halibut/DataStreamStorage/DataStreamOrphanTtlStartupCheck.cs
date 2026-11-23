using Squid.Core.Settings.Halibut;

namespace Squid.Core.Halibut.DataStreamStorage;

/// <summary>
/// The orphan sweep deletes stored streams by age, so a TTL shorter than a request may wait to be collected (and then read) would delete the data of a live request.
/// The sweep never uses such a TTL (see <see cref="DataStreamStorageOptions.WithSafeOrphanTtl"/>); this says so, once, at startup, so the operator fixes the setting.
/// </summary>
public sealed class DataStreamOrphanTtlStartupCheck(HalibutSetting setting = null) : IStartable
{
    public void Start() => Check(HalibutModule.BuildTimeoutsAndLimits().PollingRequestQueueTimeout);

    /// <summary>True when the configured TTL was too short (and the error was logged).</summary>
    internal bool Check(TimeSpan pollingQueueTimeout)
    {
        var configured = DataStreamStorageOptions.From(setting).OrphanTtl;
        var safe = DataStreamStorageOptions.SafeOrphanTtl(pollingQueueTimeout);

        if (configured >= safe) return false;

        Log.Error("Halibut:DataStreamStorage:OrphanTtlMinutes is {Configured}, shorter than the {Safe} a stored data stream must be kept (the request pickup timeout {Pickup} plus {Margin} to read it); the sweep uses {Safe} instead. Raise the setting",
            configured, safe, pollingQueueTimeout, DataStreamStorageOptions.OrphanTtlMargin);

        return true;
    }
}
