using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.Tests.Support.Fakes;

public sealed class FakeStartupHook : ITentacleStartupHook
{
    private readonly Func<CancellationToken, Task> _run;

    public FakeStartupHook(string name, Func<CancellationToken, Task> run = null)
    {
        Name = name;
        _run = run ?? (_ => Task.CompletedTask);
    }

    public string Name { get; }

    public int Calls { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        Calls++;
        await _run(ct).ConfigureAwait(false);
    }
}

public sealed class FakeBackgroundTask : ITentacleBackgroundTask
{
    private readonly Func<CancellationToken, Task> _run;

    public FakeBackgroundTask(string name, Func<CancellationToken, Task> run = null)
    {
        Name = name;
        _run = run ?? (async ct => await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false));
    }

    public string Name { get; }

    public int Calls { get; private set; }
    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task RunAsync(CancellationToken ct)
    {
        Calls++;
        Started.TrySetResult(true);
        await _run(ct).ConfigureAwait(false);
    }
}
