using Squid.Core.Services.DeploymentExecution.Lifecycle;

namespace Squid.UnitTests.Events;

public class DeploymentLifecycleDispatchTests
{
    private sealed class RecordingHandler : DeploymentLifecycleHandlerBase
    {
        public string Routed { get; private set; }

        protected override Task OnDeploymentTimedOutAsync(DeploymentEventContext ctx, CancellationToken ct) => Route(nameof(OnDeploymentTimedOutAsync));
        protected override Task OnDeploymentSucceededAsync(DeploymentEventContext ctx, CancellationToken ct) => Route(nameof(OnDeploymentSucceededAsync));
        protected override Task OnManualInterventionResolvedAsync(DeploymentEventContext ctx, CancellationToken ct) => Route(nameof(OnManualInterventionResolvedAsync));

        private Task Route(string method)
        {
            Routed = method;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Dispatch_TimedOutEvent_RoutesToTimedOutHook()
    {
        // Regression: DeploymentTimedOutEvent IS emitted by the pipeline runner but was
        // previously dropped by the base switch (fell through to the default no-op), so a
        // timed-out deployment was never audited. This pins the dispatch case that fixes it.
        var handler = new RecordingHandler();

        await handler.HandleAsync(new DeploymentTimedOutEvent(new DeploymentEventContext()), CancellationToken.None);

        handler.Routed.ShouldBe("OnDeploymentTimedOutAsync");
    }

    [Fact]
    public async Task Dispatch_SucceededEvent_RoutesToSucceededHook()
    {
        var handler = new RecordingHandler();

        await handler.HandleAsync(new DeploymentSucceededEvent(new DeploymentEventContext()), CancellationToken.None);

        handler.Routed.ShouldBe("OnDeploymentSucceededAsync");
    }

    [Fact]
    public async Task Dispatch_UnhandledEvent_IsSilentNoOp()
    {
        // A handler that does not override a hook must silently ignore the event,
        // never throw — the publisher relies on this to fan one event to many handlers.
        var handler = new RecordingHandler();

        await handler.HandleAsync(new StepStartingEvent(new DeploymentEventContext()), CancellationToken.None);

        handler.Routed.ShouldBeNull();
    }
}
