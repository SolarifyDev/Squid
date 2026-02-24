namespace Squid.Tentacle.Tests.Support;

public abstract class TimedTestBase : IDisposable
{
    private readonly CancellationTokenSource _timeoutCts;

    protected TimedTestBase(TimeSpan? timeout = null)
    {
        _timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
    }

    protected CancellationToken TestCancellationToken => _timeoutCts.Token;

    public virtual void Dispose()
    {
        _timeoutCts.Cancel();
        _timeoutCts.Dispose();
    }
}
