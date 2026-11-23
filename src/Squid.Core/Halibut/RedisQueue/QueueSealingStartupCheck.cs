using Squid.Core.Halibut.RedisQueue.Encryption;

namespace Squid.Core.Halibut.RedisQueue;

/// <summary>
/// Cross-pod delivery needs the master key: nothing is ever put into Redis unsealed. An install without one still works (every request is queued locally, as before
/// Redis was involved), so this only says, once, at startup, that cross-pod delivery cannot work. It runs in the background: sealing a key is a 600k-iteration PBKDF2.
/// </summary>
public sealed class QueueSealingStartupCheck(IQueueMessageKeyProtector protector) : IStartable
{
    public void Start() => Task.Run(Check);

    internal void Check()
    {
        try
        {
            protector.Wrap(new byte[QueueMessageEnvelope.KeyBytes]);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The master key is not available, so queued Halibut messages cannot be sealed and requests cannot cross server pods through Redis. Every request is queued on the pod that receives it; with more than one replica, a request for an agent polling another pod waits for the polling-queue timeout and fails");
        }
    }
}
