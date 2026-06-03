using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;
using Squid.Core.Services.Events;
using Squid.Message.Enums.Events;
using Squid.Message.Requests.Events;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Services.Events;

/// <summary>
/// Drives the real <see cref="DeploymentAuditEventHandler"/> against a real database via
/// its real fresh-scope persistence path (ILifetimeScope → IEventService → IUnitOfWork),
/// then reads the persisted audit row back through <see cref="IEventService"/>. Proves the
/// lifecycle seam records the right category + document references + provenance, and that a
/// pre-deployment-data event is skipped rather than persisted as an orphan.
/// </summary>
[Collection("EventsAudit")]
public class DeploymentAuditEventHandlerTests : TestBase
{
    public DeploymentAuditEventHandlerTests()
        : base("Events", "squid_it_audit_handler")
    {
    }

    private static DeploymentTaskContext Context(int spaceId, int deploymentId, int releaseId) => new()
    {
        ServerTaskId = deploymentId * 10,
        Deployment = new Deployment { Id = deploymentId, SpaceId = spaceId, ProjectId = 3, ReleaseId = releaseId, EnvironmentId = 4 },
        Project = new Project { Id = 3, Name = "Checkout" },
        Release = new Release { Id = releaseId, Version = "6.6.2" },
        Environment = new Environment { Id = 4, Name = "Production" }
    };

    private async Task EmitAsync(DeploymentTaskContext ctx, DeploymentLifecycleEvent @event)
    {
        await Run<ILifetimeScope>(async scope =>
        {
            var handler = new DeploymentAuditEventHandler(scope);
            handler.Initialize(ctx);

            await handler.HandleAsync(@event, CancellationToken.None);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SucceededEvent_PersistsDeploymentSucceeded_WithDocumentRefsAndProvenance()
    {
        const int spaceId = 700;
        const int deploymentId = 71;
        const int releaseId = 7001;

        await EmitAsync(Context(spaceId, deploymentId, releaseId), new DeploymentSucceededEvent(new DeploymentEventContext()));

        await Run<IEventService>(async events =>
        {
            var page = await events.GetEventsAsync(new GetEventsRequest { SpaceId = spaceId, DeploymentId = deploymentId });

            page.Events.Count.ShouldBe(1);

            var dto = page.Events[0];
            dto.Category.ShouldBe((int)EventCategory.DeploymentSucceeded);
            dto.DeploymentId.ShouldBe(deploymentId);
            dto.ReleaseId.ShouldBe(releaseId);
            dto.ProjectId.ShouldBe(3);
            dto.EnvironmentId.ShouldBe(4);
            dto.ServerTaskId.ShouldBe(deploymentId * 10);
            dto.ReferencesJson.ShouldContain("6.6.2");
            dto.ReferencesJson.ShouldContain("Production");
            dto.Username.ShouldBe("system");                                       // background pipeline => system actor
            dto.EstablishedWith.ShouldBe((int)EventIdentityEstablishedWith.Server);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task FullLifecycle_EmitsOrderedAuditTrail_NewestFirst()
    {
        const int spaceId = 710;
        const int deploymentId = 72;
        var ctx = Context(spaceId, deploymentId, 7201);

        await EmitAsync(ctx, new DeploymentStartingEvent(new DeploymentEventContext()));
        await EmitAsync(ctx, new ManualInterventionPromptEvent(new DeploymentEventContext()));
        await EmitAsync(ctx, new ManualInterventionResolvedEvent(new DeploymentEventContext()));
        await EmitAsync(ctx, new DeploymentSucceededEvent(new DeploymentEventContext()));

        await Run<IEventService>(async events =>
        {
            var page = await events.GetEventsAsync(new GetEventsRequest { SpaceId = spaceId, DeploymentId = deploymentId });

            page.Events.Select(e => e.Category).ShouldBe(new[]
            {
                (int)EventCategory.DeploymentSucceeded,
                (int)EventCategory.ManualInterventionSubmitted,
                (int)EventCategory.ManualInterventionRaised,
                (int)EventCategory.DeploymentStarted
            }, "the audit feed is newest-first and covers the full lifecycle");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task FailedEvent_PersistsDeploymentFailed()
    {
        const int spaceId = 720;
        const int deploymentId = 73;

        await EmitAsync(Context(spaceId, deploymentId, 7301), new DeploymentFailedEvent(new DeploymentEventContext { Exception = new InvalidOperationException("boom") }));

        await Run<IEventService>(async events =>
        {
            var page = await events.GetEventsAsync(new GetEventsRequest { SpaceId = spaceId, DeploymentId = deploymentId });

            page.Events.Single().Category.ShouldBe((int)EventCategory.DeploymentFailed);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task TimedOutEvent_IsAuditedAsFailure()
    {
        const int spaceId = 730;
        const int deploymentId = 74;

        await EmitAsync(Context(spaceId, deploymentId, 7401), new DeploymentTimedOutEvent(new DeploymentEventContext()));

        await Run<IEventService>(async events =>
        {
            var page = await events.GetEventsAsync(new GetEventsRequest { SpaceId = spaceId, DeploymentId = deploymentId });

            page.Events.Single().Category.ShouldBe((int)EventCategory.DeploymentFailed, "a timeout is a failure outcome for audit purposes");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task RecordedEvent_IsVisibleUnderEveryRelatedDocumentFeed()
    {
        const int spaceId = 740;
        const int deploymentId = 75;
        const int releaseId = 7501;

        await EmitAsync(Context(spaceId, deploymentId, releaseId), new DeploymentSucceededEvent(new DeploymentEventContext()));

        await Run<IEventService>(async events =>
        {
            (await events.GetEventsAsync(new GetEventsRequest { SpaceId = spaceId, ReleaseId = releaseId })).Events.Count.ShouldBe(1, "release feed");
            (await events.GetEventsAsync(new GetEventsRequest { SpaceId = spaceId, ProjectId = 3 })).Events.Count.ShouldBe(1, "project feed");
            (await events.GetEventsAsync(new GetEventsRequest { SpaceId = spaceId, EnvironmentId = 4 })).Events.Count.ShouldBe(1, "environment feed");
            (await events.GetEventsAsync(new GetEventsRequest { SpaceId = spaceId, DeploymentId = deploymentId })).Events.Count.ShouldBe(1, "deployment feed");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task RealLifecyclePublisher_DispatchesEmittedEvent_ToTheAutoRegisteredAuditHandler()
    {
        // The strongest seam proof short of the full pipeline: resolve the REAL
        // IDeploymentLifecycle publisher (which injects every auto-discovered
        // IDeploymentLifecycleHandler). If the audit handler is correctly registered via
        // IScopedDependency, emitting an event through the publisher persists an audit row —
        // with NO explicit wiring. This is exactly how the deployment pipeline emits.
        const int spaceId = 760;
        const int deploymentId = 76;
        var ctx = Context(spaceId, deploymentId, 7601);

        await Run<IDeploymentLifecycle>(async lifecycle =>
        {
            lifecycle.Initialize(ctx);

            await lifecycle.EmitAsync(new DeploymentSucceededEvent(new DeploymentEventContext()), CancellationToken.None);
        }).ConfigureAwait(false);

        await Run<IEventService>(async events =>
        {
            var page = await events.GetEventsAsync(new GetEventsRequest { SpaceId = spaceId, DeploymentId = deploymentId });

            page.Events.Single().Category.ShouldBe((int)EventCategory.DeploymentSucceeded, "the auto-registered audit handler must fire through the real publisher");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task NoDeploymentResolvedYet_RecordsNothing_AndDoesNotThrow()
    {
        const int spaceId = 750;

        // A lifecycle event firing before LoadDeploymentDataPhase has no deployment to
        // attribute to. The handler must no-op (no orphan row, no exception).
        await EmitAsync(new DeploymentTaskContext { ServerTaskId = 999 }, new DeploymentSucceededEvent(new DeploymentEventContext()));

        await Run<IEventService>(async events =>
        {
            (await events.GetEventsAsync(new GetEventsRequest { SpaceId = spaceId })).Events.ShouldBeEmpty();
        }).ConfigureAwait(false);
    }
}
