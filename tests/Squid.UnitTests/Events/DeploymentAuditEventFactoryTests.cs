using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Events;
using Squid.Message.Enums.Events;
using Squid.Message.Models.Events;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.UnitTests.Events;

public class DeploymentAuditEventFactoryTests
{
    private static DeploymentTaskContext FullContext() => new()
    {
        ServerTaskId = 555,
        Deployment = new Deployment { Id = 42, SpaceId = 7, ProjectId = 3, ReleaseId = 9, EnvironmentId = 4 },
        Project = new Project { Id = 3, Name = "Checkout" },
        Release = new Release { Id = 9, Version = "6.6.2" },
        Environment = new Environment { Id = 4, Name = "Production" }
    };

    [Fact]
    public void Build_ProjectsEveryDocumentReferenceFromTheDeployment()
    {
        var request = DeploymentAuditEventFactory.Build(FullContext(), EventCategory.DeploymentSucceeded);

        request.ShouldNotBeNull();
        request.Category.ShouldBe(EventCategory.DeploymentSucceeded);
        request.SpaceId.ShouldBe(7);
        request.ProjectId.ShouldBe(3);
        request.ReleaseId.ShouldBe(9);
        request.EnvironmentId.ShouldBe(4);
        request.DeploymentId.ShouldBe(42);
        request.ServerTaskId.ShouldBe(555);
        request.MachineId.ShouldBeNull("a deployment-level event is not scoped to one machine");
    }

    [Fact]
    public void Build_PopulatesReferencesWithDisplayNames_MatchingTemplateTokens()
    {
        var request = DeploymentAuditEventFactory.Build(FullContext(), EventCategory.DeploymentStarted);

        var references = request.References.ShouldBeOfType<DeploymentEventReferences>();
        references.Project.ShouldBe("Checkout");
        references.Release.ShouldBe("6.6.2");
        references.Environment.ShouldBe("Production");
    }

    [Theory]
    [InlineData(EventCategory.DeploymentStarted)]
    [InlineData(EventCategory.DeploymentResumed)]
    [InlineData(EventCategory.DeploymentSucceeded)]
    [InlineData(EventCategory.DeploymentFailed)]
    [InlineData(EventCategory.DeploymentCanceled)]
    [InlineData(EventCategory.DeploymentTimedOut)]
    [InlineData(EventCategory.ManualInterventionRaised)]
    [InlineData(EventCategory.ManualInterventionSubmitted)]
    [InlineData(EventCategory.GuidedFailureRaised)]
    public void Build_CarriesTheRequestedCategoryVerbatim(EventCategory category)
    {
        DeploymentAuditEventFactory.Build(FullContext(), category).Category.ShouldBe(category);
    }

    [Fact]
    public void Build_NoDeploymentResolvedYet_ReturnsNullSoTheHandlerSkips()
    {
        // Lifecycle events can fire before LoadDeploymentDataPhase populates ctx.Deployment.
        // There is nothing to attribute the event to, so the factory returns null (the
        // handler must skip recording rather than throw or persist an orphan).
        DeploymentAuditEventFactory.Build(new DeploymentTaskContext { ServerTaskId = 1 }, EventCategory.DeploymentFailed).ShouldBeNull();
    }

    [Fact]
    public void Build_NullContext_ReturnsNull()
    {
        DeploymentAuditEventFactory.Build(null, EventCategory.DeploymentFailed).ShouldBeNull();
    }

    [Fact]
    public void Build_MissingDisplayNames_LeavesReferenceFieldsNullButStillRecords()
    {
        var ctx = new DeploymentTaskContext { ServerTaskId = 1, Deployment = new Deployment { Id = 1, SpaceId = 2, ProjectId = 3, ReleaseId = 4, EnvironmentId = 5 } };

        var request = DeploymentAuditEventFactory.Build(ctx, EventCategory.DeploymentStarted);

        request.ShouldNotBeNull();
        request.SpaceId.ShouldBe(2);
        var references = request.References.ShouldBeOfType<DeploymentEventReferences>();
        references.Project.ShouldBeNull();
        references.Release.ShouldBeNull();
        references.Environment.ShouldBeNull();
    }
}
