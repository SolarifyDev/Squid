namespace Squid.Core.Services.DeploymentExecution.Lifecycle;

public abstract class DeploymentLifecycleHandlerBase : IDeploymentLifecycleHandler
{
    public virtual int Order => 0;

    protected DeploymentTaskContext Ctx { get; private set; }

    public void Initialize(DeploymentTaskContext ctx) => Ctx = ctx;

    public Task HandleAsync(DeploymentLifecycleEvent @event, CancellationToken ct) => @event switch
    {
        DeploymentStartingEvent e => OnDeploymentStartingAsync(e.Context, ct),
        DeploymentResumingEvent e => OnDeploymentResumingAsync(e.Context, ct),
        DeploymentSucceededEvent e => OnDeploymentSucceededAsync(e.Context, ct),
        DeploymentFailedEvent e => OnDeploymentFailedAsync(e.Context, ct),
        TargetsResolvedEvent e => OnTargetsResolvedAsync(e.Context, ct),
        UnhealthyTargetsExcludedEvent e => OnUnhealthyTargetsExcludedAsync(e.Context, ct),
        TargetPreparingEvent e => OnTargetPreparingAsync(e.Context, ct),
        TargetTransportMissingEvent e => OnTargetTransportMissingAsync(e.Context, ct),
        MachineConstraintsResolvedEvent e => OnMachineConstraintsResolvedAsync(e.Context, ct),
        PackagesAcquiringEvent e => OnPackagesAcquiringAsync(e.Context, ct),
        PackagesReleasedEvent e => OnPackagesReleasedAsync(e.Context, ct),
        StepStartingEvent e => OnStepStartingAsync(e.Context, ct),
        StepNoMatchingTargetsEvent e => OnStepNoMatchingTargetsAsync(e.Context, ct),
        StepSkippedOnTargetEvent e => OnStepSkippedOnTargetAsync(e.Context, ct),
        StepConditionMetEvent e => OnStepConditionMetAsync(e.Context, ct),
        StepExecutingOnTargetEvent e => OnStepExecutingOnTargetAsync(e.Context, ct),
        StepCompletedEvent e => OnStepCompletedAsync(e.Context, ct),
        ActionManuallyExcludedEvent e => OnActionManuallyExcludedAsync(e.Context, ct),
        ActionSkippedEvent e => OnActionSkippedAsync(e.Context, ct),
        ActionNoHandlerEvent e => OnActionNoHandlerAsync(e.Context, ct),
        ActionRunningEvent e => OnActionRunningAsync(e.Context, ct),
        ActionExecutingEvent e => OnActionExecutingAsync(e.Context, ct),
        ActionSucceededEvent e => OnActionSucceededAsync(e.Context, ct),
        ActionFailedEvent e => OnActionFailedAsync(e.Context, ct),
        ScriptOutputReceivedEvent e => OnScriptOutputReceivedAsync(e.Context, ct),
        GuidedFailurePromptEvent e => OnGuidedFailurePromptAsync(e.Context, ct),
        GuidedFailureResolvedEvent e => OnGuidedFailureResolvedAsync(e.Context, ct),
        ManualInterventionPromptEvent e => OnManualInterventionPromptAsync(e.Context, ct),
        ManualInterventionResolvedEvent e => OnManualInterventionResolvedAsync(e.Context, ct),
        DeploymentCancelledEvent e => OnDeploymentCancelledAsync(e.Context, ct),
        DeploymentPausedEvent e => OnDeploymentPausedAsync(e.Context, ct),
        _ => Task.CompletedTask
    };

    // === Deployment ===
    protected virtual Task OnDeploymentStartingAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnDeploymentResumingAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnDeploymentSucceededAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnDeploymentFailedAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;

    // === Target Preparation ===
    protected virtual Task OnTargetsResolvedAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnUnhealthyTargetsExcludedAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnTargetPreparingAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnTargetTransportMissingAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnMachineConstraintsResolvedAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;

    // === Packages ===
    protected virtual Task OnPackagesAcquiringAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnPackagesReleasedAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;

    // === Steps ===
    protected virtual Task OnStepStartingAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnStepNoMatchingTargetsAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnStepSkippedOnTargetAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnStepConditionMetAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnStepExecutingOnTargetAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnStepCompletedAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;

    // === Actions (pre-execution) ===
    protected virtual Task OnActionManuallyExcludedAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnActionSkippedAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnActionNoHandlerAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnActionRunningAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;

    // === Actions (execution) ===
    protected virtual Task OnActionExecutingAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnActionSucceededAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnActionFailedAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;

    // === Script Output ===
    protected virtual Task OnScriptOutputReceivedAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;

    // === Guided Failure ===
    protected virtual Task OnGuidedFailurePromptAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnGuidedFailureResolvedAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;

    // === Manual Intervention ===
    protected virtual Task OnManualInterventionPromptAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnManualInterventionResolvedAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;

    // === Cancellation / Pause ===
    protected virtual Task OnDeploymentCancelledAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
    protected virtual Task OnDeploymentPausedAsync(DeploymentEventContext ctx, CancellationToken ct) => Task.CompletedTask;
}
