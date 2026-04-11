using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Planning;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// Phase 6c-i — unit tests for <see cref="PlanDeploymentPhase"/>. The phase is a thin
/// shim that assembles a <see cref="DeploymentPlanRequest"/> from the task context and
/// stores the resulting <see cref="DeploymentPlan"/> on <c>ctx.Plan</c>. In this phase
/// the plan is observed but not consumed, so the tests focus on:
///   - the request is built with every field the planner expects,
///   - the plan is stashed on the context,
///   - the server-only and empty-steps shortcuts are honoured,
///   - Preview mode is used so blockers never short-circuit execution.
/// </summary>
public class PlanDeploymentPhaseTests
{
    [Fact]
    public async Task ExecuteAsync_PopulatesCtxPlanWithPlannerResult()
    {
        var plan = new DeploymentPlan
        {
            Mode = PlanMode.Preview,
            ReleaseId = 1,
            EnvironmentId = 2,
            DeploymentProcessSnapshotId = 3
        };

        var planner = new Mock<IDeploymentPlanner>();
        planner.Setup(p => p.PlanAsync(It.IsAny<DeploymentPlanRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        var ctx = BuildContext();

        await new PlanDeploymentPhase(planner.Object).ExecuteAsync(ctx, CancellationToken.None);

        ctx.Plan.ShouldBe(plan);
    }

    [Fact]
    public async Task ExecuteAsync_BuildsRequestWithAllRequiredFields()
    {
        DeploymentPlanRequest captured = null;

        var planner = new Mock<IDeploymentPlanner>();
        planner.Setup(p => p.PlanAsync(It.IsAny<DeploymentPlanRequest>(), It.IsAny<CancellationToken>()))
            .Callback((DeploymentPlanRequest req, CancellationToken _) => captured = req)
            .ReturnsAsync(new DeploymentPlan
            {
                Mode = PlanMode.Preview,
                ReleaseId = 0,
                EnvironmentId = 0,
                DeploymentProcessSnapshotId = 0
            });

        var ctx = BuildContext();

        await new PlanDeploymentPhase(planner.Object).ExecuteAsync(ctx, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.Mode.ShouldBe(PlanMode.Preview);
        captured.ReleaseId.ShouldBe(3001);
        captured.EnvironmentId.ShouldBe(42);
        captured.ChannelId.ShouldBe(77);
        captured.DeploymentProcessSnapshotId.ShouldBe(9001);
        captured.DeploymentId.ShouldBe(2001);
        captured.ServerTaskId.ShouldBe(1001);
        captured.Steps.ShouldBe(ctx.Steps);
        captured.Variables.ShouldBe(ctx.Variables);
        captured.TargetContexts.ShouldBe(ctx.AllTargetsContext);
    }

    [Fact]
    public async Task ExecuteAsync_UsesPreviewModeSoBlockersDoNotThrow()
    {
        DeploymentPlanRequest captured = null;

        var planner = new Mock<IDeploymentPlanner>();
        planner.Setup(p => p.PlanAsync(It.IsAny<DeploymentPlanRequest>(), It.IsAny<CancellationToken>()))
            .Callback((DeploymentPlanRequest req, CancellationToken _) => captured = req)
            .ReturnsAsync(new DeploymentPlan
            {
                Mode = PlanMode.Preview,
                ReleaseId = 0,
                EnvironmentId = 0,
                DeploymentProcessSnapshotId = 0
            });

        var ctx = BuildContext();

        await new PlanDeploymentPhase(planner.Object).ExecuteAsync(ctx, CancellationToken.None);

        captured.Mode.ShouldBe(PlanMode.Preview);
    }

    [Fact]
    public async Task ExecuteAsync_ServerOnlyDeployment_SkipsPlannerAndLeavesPlanNull()
    {
        var planner = new Mock<IDeploymentPlanner>();
        var ctx = BuildContext();
        ctx.IsServerOnlyDeployment = true;

        await new PlanDeploymentPhase(planner.Object).ExecuteAsync(ctx, CancellationToken.None);

        ctx.Plan.ShouldBeNull();
        planner.Verify(p => p.PlanAsync(It.IsAny<DeploymentPlanRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_EmptySteps_SkipsPlannerAndLeavesPlanNull()
    {
        var planner = new Mock<IDeploymentPlanner>();
        var ctx = BuildContext();
        ctx.Steps = new List<DeploymentStepDto>();

        await new PlanDeploymentPhase(planner.Object).ExecuteAsync(ctx, CancellationToken.None);

        ctx.Plan.ShouldBeNull();
        planner.Verify(p => p.PlanAsync(It.IsAny<DeploymentPlanRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_NullSteps_SkipsPlannerAndLeavesPlanNull()
    {
        var planner = new Mock<IDeploymentPlanner>();
        var ctx = BuildContext();
        ctx.Steps = null;

        await new PlanDeploymentPhase(planner.Object).ExecuteAsync(ctx, CancellationToken.None);

        ctx.Plan.ShouldBeNull();
        planner.Verify(p => p.PlanAsync(It.IsAny<DeploymentPlanRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationIsForwardedToPlanner()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var planner = new Mock<IDeploymentPlanner>();
        planner.Setup(p => p.PlanAsync(It.IsAny<DeploymentPlanRequest>(), It.IsAny<CancellationToken>()))
            .Returns<DeploymentPlanRequest, CancellationToken>((_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new DeploymentPlan
                {
                    Mode = PlanMode.Preview,
                    ReleaseId = 0,
                    EnvironmentId = 0,
                    DeploymentProcessSnapshotId = 0
                });
            });

        var ctx = BuildContext();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            new PlanDeploymentPhase(planner.Object).ExecuteAsync(ctx, cts.Token));
    }

    [Fact]
    public void PlanDeploymentPhase_Order_Is460_BetweenAnnounceAndExecute()
    {
        var phase = new PlanDeploymentPhase(Mock.Of<IDeploymentPlanner>());

        phase.Order.ShouldBe(460);
        phase.Order.ShouldBeGreaterThan(new AnnounceSetupPhase(null!).Order);
        phase.Order.ShouldBeLessThan(500);
    }

    // ---------- test infrastructure -------------------------------------

    private static DeploymentTaskContext BuildContext()
    {
        var step = new DeploymentStepDto
        {
            Id = 10,
            StepOrder = 1,
            Name = "Deploy",
            Properties = new List<DeploymentStepPropertyDto>(),
            Actions =
            {
                new DeploymentActionDto
                {
                    Id = 100,
                    ActionOrder = 1,
                    ActionType = SpecialVariables.ActionTypes.Script,
                    Name = "Run"
                }
            }
        };

        return new DeploymentTaskContext
        {
            ServerTaskId = 1001,
            Deployment = new Deployment { Id = 2001, EnvironmentId = 42, ChannelId = 77 },
            Release = new Squid.Core.Persistence.Entities.Deployments.Release { Id = 3001, Version = "1.0.0" },
            ProcessSnapshot = new DeploymentProcessSnapshotDto { Id = 9001 },
            Steps = new List<DeploymentStepDto> { step },
            Variables = new List<VariableDto>(),
            AllTargetsContext = new List<DeploymentTargetContext>
            {
                new()
                {
                    Machine = new Machine { Id = 1, Name = "web-1", Roles = "[]" },
                    CommunicationStyle = CommunicationStyle.KubernetesApi,
                    Transport = new StubTransport()
                }
            }
        };
    }

    private sealed class StubTransport : IDeploymentTransport
    {
        public CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesApi;
        public IEndpointVariableContributor Variables => null;
        public IScriptContextWrapper ScriptWrapper => null;
        public IExecutionStrategy Strategy => null;
        public IHealthCheckStrategy HealthChecker => null;
        public ExecutionLocation ExecutionLocation => ExecutionLocation.Unspecified;
        public ExecutionBackend ExecutionBackend => ExecutionBackend.Unspecified;
        public bool RequiresContextPreparationForPackagedPayload => false;
    }
}
