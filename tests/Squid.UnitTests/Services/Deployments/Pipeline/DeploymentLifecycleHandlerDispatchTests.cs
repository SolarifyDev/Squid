using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class DeploymentLifecycleHandlerDispatchTests
{
    private readonly TrackingHandler _handler = new();

    public static IEnumerable<object[]> AllEventDispatchCases => new List<object[]>
    {
        new object[] { new DeploymentStartingEvent(new DeploymentEventContext()), "OnDeploymentStartingAsync" },
        new object[] { new DeploymentResumingEvent(new DeploymentEventContext()), "OnDeploymentResumingAsync" },
        new object[] { new DeploymentSucceededEvent(new DeploymentEventContext()), "OnDeploymentSucceededAsync" },
        new object[] { new DeploymentFailedEvent(new DeploymentEventContext()), "OnDeploymentFailedAsync" },
        new object[] { new TargetsResolvedEvent(new DeploymentEventContext()), "OnTargetsResolvedAsync" },
        new object[] { new UnhealthyTargetsExcludedEvent(new DeploymentEventContext()), "OnUnhealthyTargetsExcludedAsync" },
        new object[] { new TargetPreparingEvent(new DeploymentEventContext()), "OnTargetPreparingAsync" },
        new object[] { new TargetTransportMissingEvent(new DeploymentEventContext()), "OnTargetTransportMissingAsync" },
        new object[] { new MachineConstraintsResolvedEvent(new DeploymentEventContext()), "OnMachineConstraintsResolvedAsync" },
        new object[] { new PackagesAcquiringEvent(new DeploymentEventContext()), "OnPackagesAcquiringAsync" },
        new object[] { new PackagesReleasedEvent(new DeploymentEventContext()), "OnPackagesReleasedAsync" },
        new object[] { new StepStartingEvent(new DeploymentEventContext()), "OnStepStartingAsync" },
        new object[] { new StepNoMatchingTargetsEvent(new DeploymentEventContext()), "OnStepNoMatchingTargetsAsync" },
        new object[] { new StepSkippedOnTargetEvent(new DeploymentEventContext()), "OnStepSkippedOnTargetAsync" },
        new object[] { new StepConditionMetEvent(new DeploymentEventContext()), "OnStepConditionMetAsync" },
        new object[] { new StepExecutingOnTargetEvent(new DeploymentEventContext()), "OnStepExecutingOnTargetAsync" },
        new object[] { new StepCompletedEvent(new DeploymentEventContext()), "OnStepCompletedAsync" },
        new object[] { new HealthCheckStartingEvent(new DeploymentEventContext()), "OnHealthCheckStartingAsync" },
        new object[] { new HealthCheckTargetResultEvent(new DeploymentEventContext()), "OnHealthCheckTargetResultAsync" },
        new object[] { new HealthCheckCompletedEvent(new DeploymentEventContext()), "OnHealthCheckCompletedAsync" },
        new object[] { new ActionManuallyExcludedEvent(new DeploymentEventContext()), "OnActionManuallyExcludedAsync" },
        new object[] { new ActionSkippedEvent(new DeploymentEventContext()), "OnActionSkippedAsync" },
        new object[] { new ActionNoHandlerEvent(new DeploymentEventContext()), "OnActionNoHandlerAsync" },
        new object[] { new ActionRunningEvent(new DeploymentEventContext()), "OnActionRunningAsync" },
        new object[] { new ActionExecutingEvent(new DeploymentEventContext()), "OnActionExecutingAsync" },
        new object[] { new ActionSucceededEvent(new DeploymentEventContext()), "OnActionSucceededAsync" },
        new object[] { new ActionFailedEvent(new DeploymentEventContext()), "OnActionFailedAsync" },
        new object[] { new ScriptOutputReceivedEvent(new DeploymentEventContext()), "OnScriptOutputReceivedAsync" },
        new object[] { new GuidedFailurePromptEvent(new DeploymentEventContext()), "OnGuidedFailurePromptAsync" },
        new object[] { new GuidedFailureResolvedEvent(new DeploymentEventContext()), "OnGuidedFailureResolvedAsync" },
        new object[] { new ManualInterventionPromptEvent(new DeploymentEventContext()), "OnManualInterventionPromptAsync" },
        new object[] { new ManualInterventionResolvedEvent(new DeploymentEventContext()), "OnManualInterventionResolvedAsync" },
        new object[] { new DeploymentCancelledEvent(new DeploymentEventContext()), "OnDeploymentCancelledAsync" },
        new object[] { new DeploymentPausedEvent(new DeploymentEventContext()), "OnDeploymentPausedAsync" },
    };

    [Theory]
    [MemberData(nameof(AllEventDispatchCases))]
    public async Task HandleAsync_DispatchesToCorrectVirtualMethod(DeploymentLifecycleEvent @event, string expectedMethod)
    {
        await _handler.HandleAsync(@event, CancellationToken.None);

        _handler.LastCalledMethod.ShouldBe(expectedMethod);
    }

    [Fact]
    public async Task HandleAsync_UnknownEventType_NoMethodCalled()
    {
        var unknownEvent = new DeploymentTimedOutEvent(new DeploymentEventContext());

        await _handler.HandleAsync(unknownEvent, CancellationToken.None);

        _handler.LastCalledMethod.ShouldBeNull();
    }

    [Fact]
    public void Initialize_SetsCtxProperty()
    {
        var ctx = new DeploymentTaskContext();

        _handler.Initialize(ctx);

        _handler.ExposedCtx.ShouldBeSameAs(ctx);
    }

    [Fact]
    public void Order_DefaultsToZero()
    {
        _handler.Order.ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(AllEventDispatchCases))]
    public async Task HandleAsync_PassesEventContextToVirtualMethod(DeploymentLifecycleEvent @event, string _)
    {
        await _handler.HandleAsync(@event, CancellationToken.None);

        _handler.LastReceivedContext.ShouldBeSameAs(@event.Context);
    }

    private class TrackingHandler : DeploymentLifecycleHandlerBase
    {
        public string LastCalledMethod { get; private set; }
        public DeploymentEventContext LastReceivedContext { get; private set; }
        public DeploymentTaskContext ExposedCtx => Ctx;

        private Task Track(string method, DeploymentEventContext ctx)
        {
            LastCalledMethod = method;
            LastReceivedContext = ctx;
            return Task.CompletedTask;
        }

        // === Deployment ===
        protected override Task OnDeploymentStartingAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnDeploymentStartingAsync), ctx);
        protected override Task OnDeploymentResumingAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnDeploymentResumingAsync), ctx);
        protected override Task OnDeploymentSucceededAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnDeploymentSucceededAsync), ctx);
        protected override Task OnDeploymentFailedAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnDeploymentFailedAsync), ctx);

        // === Target Preparation ===
        protected override Task OnTargetsResolvedAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnTargetsResolvedAsync), ctx);
        protected override Task OnUnhealthyTargetsExcludedAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnUnhealthyTargetsExcludedAsync), ctx);
        protected override Task OnTargetPreparingAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnTargetPreparingAsync), ctx);
        protected override Task OnTargetTransportMissingAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnTargetTransportMissingAsync), ctx);
        protected override Task OnMachineConstraintsResolvedAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnMachineConstraintsResolvedAsync), ctx);

        // === Packages ===
        protected override Task OnPackagesAcquiringAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnPackagesAcquiringAsync), ctx);
        protected override Task OnPackagesReleasedAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnPackagesReleasedAsync), ctx);

        // === Steps ===
        protected override Task OnStepStartingAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnStepStartingAsync), ctx);
        protected override Task OnStepNoMatchingTargetsAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnStepNoMatchingTargetsAsync), ctx);
        protected override Task OnStepSkippedOnTargetAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnStepSkippedOnTargetAsync), ctx);
        protected override Task OnStepConditionMetAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnStepConditionMetAsync), ctx);
        protected override Task OnStepExecutingOnTargetAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnStepExecutingOnTargetAsync), ctx);
        protected override Task OnStepCompletedAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnStepCompletedAsync), ctx);

        // === Health Check ===
        protected override Task OnHealthCheckStartingAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnHealthCheckStartingAsync), ctx);
        protected override Task OnHealthCheckTargetResultAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnHealthCheckTargetResultAsync), ctx);
        protected override Task OnHealthCheckCompletedAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnHealthCheckCompletedAsync), ctx);

        // === Actions (pre-execution) ===
        protected override Task OnActionManuallyExcludedAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnActionManuallyExcludedAsync), ctx);
        protected override Task OnActionSkippedAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnActionSkippedAsync), ctx);
        protected override Task OnActionNoHandlerAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnActionNoHandlerAsync), ctx);
        protected override Task OnActionRunningAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnActionRunningAsync), ctx);

        // === Actions (execution) ===
        protected override Task OnActionExecutingAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnActionExecutingAsync), ctx);
        protected override Task OnActionSucceededAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnActionSucceededAsync), ctx);
        protected override Task OnActionFailedAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnActionFailedAsync), ctx);

        // === Script Output ===
        protected override Task OnScriptOutputReceivedAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnScriptOutputReceivedAsync), ctx);

        // === Guided Failure ===
        protected override Task OnGuidedFailurePromptAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnGuidedFailurePromptAsync), ctx);
        protected override Task OnGuidedFailureResolvedAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnGuidedFailureResolvedAsync), ctx);

        // === Manual Intervention ===
        protected override Task OnManualInterventionPromptAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnManualInterventionPromptAsync), ctx);
        protected override Task OnManualInterventionResolvedAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnManualInterventionResolvedAsync), ctx);

        // === Cancellation / Pause ===
        protected override Task OnDeploymentCancelledAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnDeploymentCancelledAsync), ctx);
        protected override Task OnDeploymentPausedAsync(DeploymentEventContext ctx, CancellationToken ct) => Track(nameof(OnDeploymentPausedAsync), ctx);
    }
}
