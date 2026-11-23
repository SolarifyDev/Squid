using System.Collections.Concurrent;

namespace Squid.Core.Services.DeploymentExecution.Script;

public interface ITaskCancellationRegistry : ISingletonDependency
{
    /// <summary>
    /// Creates this run's own token source and installs it as the task's cancel target ONLY when no other run is registered. It never
    /// disposes or displaces another run's source: a duplicate delivery of a task a live run owns would otherwise kill that run's token.
    /// </summary>
    CancellationTokenSource Register(int serverTaskId);

    /// <summary>
    /// Makes <paramref name="cts"/> the task's cancel target once its run has actually claimed the task (and so fenced out any other
    /// owner). The replaced source is left alone — its own run disposes it.
    /// </summary>
    void Promote(int serverTaskId, CancellationTokenSource cts);

    bool TryCancel(int serverTaskId);

    void Unregister(int serverTaskId);

    /// <summary>Removes the task's entry only when it is still <paramref name="cts"/>, then disposes <paramref name="cts"/> (always the caller's own).</summary>
    void Unregister(int serverTaskId, CancellationTokenSource cts);
}

public class TaskCancellationRegistry : ITaskCancellationRegistry
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _tokens = new();

    public CancellationTokenSource Register(int serverTaskId)
    {
        var cts = new CancellationTokenSource();

        _tokens.TryAdd(serverTaskId, cts);

        return cts;
    }

    public void Promote(int serverTaskId, CancellationTokenSource cts) => _tokens[serverTaskId] = cts;

    public bool TryCancel(int serverTaskId)
    {
        if (!_tokens.TryGetValue(serverTaskId, out var cts)) return false;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    public void Unregister(int serverTaskId)
    {
        if (_tokens.TryRemove(serverTaskId, out var cts))
            cts.Dispose();
    }

    public void Unregister(int serverTaskId, CancellationTokenSource cts)
    {
        ((ICollection<KeyValuePair<int, CancellationTokenSource>>)_tokens).Remove(new KeyValuePair<int, CancellationTokenSource>(serverTaskId, cts));

        cts.Dispose();
    }
}
