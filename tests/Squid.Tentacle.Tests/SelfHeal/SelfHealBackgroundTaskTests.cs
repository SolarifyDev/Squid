using Shouldly;
using Squid.Tentacle.SelfHeal;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.SelfHeal;

/// <summary>
/// Pins <see cref="SelfHealBackgroundTask"/> — the adapter that schedules the
/// self-heal loops on the host's <c>ITentacleBackgroundTask</c> lifecycle. This is
/// the wiring that takes the heal scaffolding from dead code to live; the test
/// proves the controller's actions actually fire under RunAsync and that
/// cancellation drains cleanly (so host shutdown doesn't hang).
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class SelfHealBackgroundTaskTests
{
    [Fact]
    public void ForLocalWorkspaces_ProducesNamedSelfHealTask()
    {
        var task = SelfHealBackgroundTask.ForLocalWorkspaces(reporter: null);

        task.Name.ShouldBe("SelfHeal");
    }

    [Fact]
    public async Task RunAsync_StartsHealActions_AndDrainsOnCancellation()
    {
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = new SelfHealBackgroundTask(new SelfHealController(new ISelfHealAction[] { new ProbeAction(fired) }));

        using var cts = new CancellationTokenSource();
        var run = task.RunAsync(cts.Token);

        // The action loop must actually run — proves Start() was invoked.
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();

        // RunAsync must return promptly on cancellation (drains the controller),
        // never hang the host shutdown.
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        run.IsCompletedSuccessfully.ShouldBeTrue();
    }

    private sealed class ProbeAction : ISelfHealAction
    {
        private readonly TaskCompletionSource _fired;

        public ProbeAction(TaskCompletionSource fired) => _fired = fired;

        public string Name => "probe";

        public TimeSpan CheckInterval => TimeSpan.FromMilliseconds(10);

        public Task<SelfHealOutcome> RunAsync(CancellationToken ct)
        {
            _fired.TrySetResult();
            return Task.FromResult(SelfHealOutcome.Healthy(Name));
        }
    }
}
