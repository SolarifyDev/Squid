using Serilog;

namespace Squid.Tentacle.SelfHeal;

/// <summary>
/// Schedules a set of <see cref="ISelfHealAction"/>s to run periodically. Each
/// action has its own cadence; the controller spawns one task per action so a
/// slow heal can't starve others. Exceptions are logged and swallowed — a
/// broken heal action must never crash the agent host.
/// </summary>
public sealed class SelfHealController : IAsyncDisposable
{
    private readonly IReadOnlyList<ISelfHealAction> _actions;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = new();
    private int _started;
    private int _disposed;

    public SelfHealController(IEnumerable<ISelfHealAction> actions)
    {
        _actions = (actions ?? throw new ArgumentNullException(nameof(actions))).ToList();
    }

    public int ActionCount => _actions.Count;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        foreach (var action in _actions)
            _loops.Add(Task.Run(() => RunLoopAsync(action, _cts.Token)));

        Log.Information("[SelfHeal] Started {Count} heal action(s)", _actions.Count);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _cts.Cancel(); }
        catch { /* ignore */ }

        try { await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* best-effort drain */ }

        _cts.Dispose();
    }

    private async Task RunLoopAsync(ISelfHealAction action, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var outcome = await action.RunAsync(ct).ConfigureAwait(false);
                if (outcome.Healed)
                    Log.Information("[SelfHeal] {Action}: {Message}", outcome.Action, outcome.Message);
                else
                    Log.Debug("[SelfHeal] {Action}: {Message}", outcome.Action, outcome.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[SelfHeal] Action {Name} threw; continuing on schedule", action.Name);
            }

            try { await Task.Delay(action.CheckInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
}
