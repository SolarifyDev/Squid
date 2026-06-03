using Squid.Core.Persistence.Db;
using Squid.Core.Services.Events;
using Squid.Message.Enums.Events;

namespace Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;

/// <summary>
/// Records deployment-lifecycle audit events into the persisted Event stream — one of the
/// two central, generic emission points for the audit history (the other being the EF
/// SaveChanges document interceptor). It subscribes to the SAME lifecycle events the
/// pipeline already emits, so there are NO scattered RecordAsync calls and no producer
/// changes: each milestone maps to exactly one <see cref="EventCategory"/>.
///
/// <para>Audit writes run in a fresh DI scope so they persist independently of the
/// deployment's own transaction (append-only audit semantics — a rolled-back step must
/// not erase the fact that it ran). The lifecycle publisher already isolates handler
/// exceptions, so an audit-write failure can never block or fail the deployment.</para>
/// </summary>
public sealed class DeploymentAuditEventHandler : DeploymentLifecycleHandlerBase
{
    // Runs after the activity logger (Order 0); audit is a terminal side-effect.
    public override int Order => 1000;

    private readonly ILifetimeScope _scope;

    public DeploymentAuditEventHandler(ILifetimeScope scope) => _scope = scope;

    protected override Task OnDeploymentStartingAsync(DeploymentEventContext ctx, CancellationToken ct) => RecordAsync(EventCategory.DeploymentStarted, ct);

    protected override Task OnDeploymentResumingAsync(DeploymentEventContext ctx, CancellationToken ct) => RecordAsync(EventCategory.DeploymentResumed, ct);

    protected override Task OnDeploymentSucceededAsync(DeploymentEventContext ctx, CancellationToken ct) => RecordAsync(EventCategory.DeploymentSucceeded, ct);

    protected override Task OnDeploymentFailedAsync(DeploymentEventContext ctx, CancellationToken ct) => RecordAsync(EventCategory.DeploymentFailed, ct);

    // A timeout is a failure outcome for audit purposes.
    protected override Task OnDeploymentTimedOutAsync(DeploymentEventContext ctx, CancellationToken ct) => RecordAsync(EventCategory.DeploymentFailed, ct);

    protected override Task OnDeploymentCancelledAsync(DeploymentEventContext ctx, CancellationToken ct) => RecordAsync(EventCategory.DeploymentCanceled, ct);

    protected override Task OnManualInterventionPromptAsync(DeploymentEventContext ctx, CancellationToken ct) => RecordAsync(EventCategory.ManualInterventionRaised, ct);

    protected override Task OnManualInterventionResolvedAsync(DeploymentEventContext ctx, CancellationToken ct) => RecordAsync(EventCategory.ManualInterventionSubmitted, ct);

    protected override Task OnGuidedFailurePromptAsync(DeploymentEventContext ctx, CancellationToken ct) => RecordAsync(EventCategory.GuidedFailureRaised, ct);

    private async Task RecordAsync(EventCategory category, CancellationToken ct)
    {
        var request = DeploymentAuditEventFactory.Build(Ctx, category);

        if (request == null) return;

        await using var scope = _scope.BeginLifetimeScope();

        var events = scope.Resolve<IEventService>();
        var unitOfWork = scope.Resolve<IUnitOfWork>();

        await events.RecordAsync(request, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
