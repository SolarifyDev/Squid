using System.Collections.Concurrent;

namespace Squid.Core.Services.DeploymentExecution;

public interface ITaskCancellationRegistry : ISingletonDependency
{
    CancellationTokenSource Register(int serverTaskId);
    bool TryCancel(int serverTaskId);
    void Unregister(int serverTaskId);
}

public class TaskCancellationRegistry : ITaskCancellationRegistry
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _tokens = new();

    public CancellationTokenSource Register(int serverTaskId)
    {
        var cts = new CancellationTokenSource();

        var old = _tokens.AddOrUpdate(serverTaskId, cts, (_, existing) =>
        {
            existing.Dispose();
            return cts;
        });

        return cts;
    }

    public bool TryCancel(int serverTaskId)
    {
        if (!_tokens.TryGetValue(serverTaskId, out var cts)) return false;

        cts.Cancel();

        return true;
    }

    public void Unregister(int serverTaskId)
    {
        if (_tokens.TryRemove(serverTaskId, out var cts))
            cts.Dispose();
    }
}
