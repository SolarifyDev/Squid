using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.Tests.Support.Lifecycle;

// Test harness mirroring Program.cs lifecycle semantics so startup hooks / background tasks
// can be exercised in isolation without launching the top-level process.
public static class TentacleLifecycleHarness
{
    public static async Task RunStartupHooksAsync(
        IReadOnlyList<ITentacleStartupHook> hooks,
        CancellationToken ct)
    {
        foreach (var hook in hooks)
        {
            ct.ThrowIfCancellationRequested();
            await hook.RunAsync(ct).ConfigureAwait(false);
        }
    }

    public static IReadOnlyList<Task> StartBackgroundTasks(
        IReadOnlyList<ITentacleBackgroundTask> tasks,
        CancellationToken ct)
    {
        var running = new List<Task>(tasks.Count);

        foreach (var task in tasks)
        {
            running.Add(Task.Run(() => task.RunAsync(ct), ct));
        }

        return running;
    }
}
