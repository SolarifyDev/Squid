using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Fakes;

namespace Squid.Tentacle.Tests.Support.Lifecycle;

[Trait("Category", TentacleTestCategories.Lifecycle)]
public class TentacleLifecycleHarnessTests : TimedTestBase
{
    [Fact]
    public async Task RunStartupHooksAsync_Runs_Hooks_In_Order()
    {
        var executionOrder = new List<string>();
        var hooks = new[]
        {
            new FakeStartupHook("First", _ =>
            {
                executionOrder.Add("First");
                return Task.CompletedTask;
            }),
            new FakeStartupHook("Second", _ =>
            {
                executionOrder.Add("Second");
                return Task.CompletedTask;
            })
        };

        await TentacleLifecycleHarness.RunStartupHooksAsync(hooks, TestCancellationToken);

        executionOrder.ShouldBe(new[] { "First", "Second" });
        hooks[0].Calls.ShouldBe(1);
        hooks[1].Calls.ShouldBe(1);
    }

    [Fact]
    public async Task StartBackgroundTasks_Starts_And_Cancels_Running_Tasks()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var task1 = new FakeBackgroundTask("Task1");
        var task2 = new FakeBackgroundTask("Task2");

        var running = TentacleLifecycleHarness.StartBackgroundTasks(new[] { task1, task2 }, cts.Token);

        await task1.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestCancellationToken);
        await task2.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestCancellationToken);

        task1.Calls.ShouldBe(1);
        task2.Calls.ShouldBe(1);

        cts.Cancel();

        await Should.ThrowAsync<TaskCanceledException>(async () =>
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(2), TestCancellationToken));

        running.All(t => t.IsCanceled || t.IsCompleted).ShouldBeTrue();
    }
}
