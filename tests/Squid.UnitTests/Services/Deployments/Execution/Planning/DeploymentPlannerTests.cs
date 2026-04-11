using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Planning;
using Squid.Core.Services.DeploymentExecution.Planning.Exceptions;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Execution.Planning;

/// <summary>
/// Phase 6a — unit tests for <see cref="DeploymentPlanner"/>. Exercises step applicability,
/// role-based target matching, per-dispatch capability validation, global blocker
/// aggregation, and the Execute-mode throw behaviour.
/// </summary>
public class DeploymentPlannerTests
{
    private readonly CapabilityValidator _validator = new();
    private readonly Mock<IActionHandlerRegistry> _registry = new();

    public DeploymentPlannerTests()
    {
        _registry.Setup(r => r.ResolveScope(It.IsAny<DeploymentActionDto>()))
            .Returns(ExecutionScope.TargetLevel);
    }

    private DeploymentPlanner BuildPlanner() => new(_registry.Object, _validator);

    // ---------- guard clauses -------------------------------------------

    [Fact]
    public async Task PlanAsync_NullRequest_Throws()
    {
        var planner = BuildPlanner();

        await Should.ThrowAsync<ArgumentNullException>(() =>
            planner.PlanAsync(null!, CancellationToken.None));
    }

    // ---------- happy path ----------------------------------------------

    [Fact]
    public async Task PlanAsync_SingleApplicableStep_ProducesDispatchPerTarget()
    {
        var step = BuildStep(id: 10, order: 1, name: "Deploy", roles: "web");
        step.Actions.Add(BuildAction(id: 100, order: 1, actionType: SpecialVariables.ActionTypes.Script, name: "Run"));

        var targets = new[]
        {
            BuildTargetContext(1, "web-1", "web", CommunicationStyle.KubernetesApi),
            BuildTargetContext(2, "web-2", "web", CommunicationStyle.KubernetesApi)
        };

        var plan = await BuildPlanner().PlanAsync(BuildRequest(PlanMode.Preview, [step], targets), CancellationToken.None);

        plan.CanProceed.ShouldBeTrue();
        plan.Steps.Count.ShouldBe(1);

        var planned = plan.Steps[0];
        planned.Status.ShouldBe(PlannedStepStatus.Applicable);
        planned.MatchedTargets.Select(t => t.MachineId).ShouldBe(new[] { 1, 2 }, ignoreOrder: true);
        planned.Actions.Count.ShouldBe(1);
        planned.Dispatches.Count.ShouldBe(2);
        planned.Actions[0].Dispatches.All(d => d.Validation.IsValid).ShouldBeTrue();
    }

    // ---------- disabled / no-runnable / step-level / run-on-server ----

    [Fact]
    public async Task PlanAsync_DisabledStep_IsSkippedWithDisabledStatus()
    {
        var step = BuildStep(id: 10, order: 1, name: "Deploy");
        step.IsDisabled = true;
        step.Actions.Add(BuildAction(id: 100, order: 1, actionType: SpecialVariables.ActionTypes.Script, name: "Run"));

        var targets = new[] { BuildTargetContext(1, "web-1", "web", CommunicationStyle.KubernetesApi) };

        var plan = await BuildPlanner().PlanAsync(BuildRequest(PlanMode.Preview, [step], targets), CancellationToken.None);

        plan.Steps.Single().Status.ShouldBe(PlannedStepStatus.Disabled);
        plan.BlockingReasons.ShouldBeEmpty();
    }

    [Fact]
    public async Task PlanAsync_StepWithActionSkippedById_IsMarkedNoRunnableActions()
    {
        var step = BuildStep(id: 10, order: 1, name: "Deploy");
        step.Actions.Add(BuildAction(id: 100, order: 1, actionType: SpecialVariables.ActionTypes.Script, name: "Run"));

        var targets = new[] { BuildTargetContext(1, "web-1", "web", CommunicationStyle.KubernetesApi) };
        var request = BuildRequest(PlanMode.Preview, [step], targets) with
        {
            SkipActionIds = new HashSet<int> { 100 }
        };

        var plan = await BuildPlanner().PlanAsync(request, CancellationToken.None);

        plan.Steps.Single().Status.ShouldBe(PlannedStepStatus.NoRunnableActions);
    }

    [Fact]
    public async Task PlanAsync_AllActionsStepLevel_StepLevelOnlyNoDispatches()
    {
        var step = BuildStep(id: 10, order: 1, name: "Manual");
        var action = BuildAction(id: 100, order: 1, actionType: "Squid.ManualIntervention", name: "Approve");
        step.Actions.Add(action);

        _registry.Setup(r => r.ResolveScope(action)).Returns(ExecutionScope.StepLevel);

        var targets = new[] { BuildTargetContext(1, "web-1", "web", CommunicationStyle.KubernetesApi) };

        var plan = await BuildPlanner().PlanAsync(BuildRequest(PlanMode.Preview, [step], targets), CancellationToken.None);

        var planned = plan.Steps.Single();
        planned.Status.ShouldBe(PlannedStepStatus.StepLevelOnly);
        planned.Actions.Count.ShouldBe(1);
        planned.Actions[0].IsStepLevel.ShouldBeTrue();
        planned.Dispatches.Count.ShouldBe(0);
    }

    [Fact]
    public async Task PlanAsync_RunOnServerStep_IsMarkedRunOnServer()
    {
        var step = BuildStep(id: 10, order: 1, name: "ServerSide");
        step.Properties.Add(new DeploymentStepPropertyDto
        {
            PropertyName = SpecialVariables.Step.RunOnServer,
            PropertyValue = "true"
        });
        step.Actions.Add(BuildAction(id: 100, order: 1, actionType: SpecialVariables.ActionTypes.Script, name: "Run"));

        var targets = new[] { BuildTargetContext(1, "web-1", "web", CommunicationStyle.KubernetesApi) };

        var plan = await BuildPlanner().PlanAsync(BuildRequest(PlanMode.Preview, [step], targets), CancellationToken.None);

        var planned = plan.Steps.Single();
        planned.Status.ShouldBe(PlannedStepStatus.RunOnServer);
        planned.Dispatches.Count.ShouldBe(0);
        plan.BlockingReasons.ShouldBeEmpty();
    }

    // ---------- role matching -------------------------------------------

    [Fact]
    public async Task PlanAsync_StepRequiresRoles_OnlyMatchingTargetsDispatched()
    {
        var step = BuildStep(id: 10, order: 1, name: "Deploy", roles: "web");
        step.Actions.Add(BuildAction(id: 100, order: 1, actionType: SpecialVariables.ActionTypes.Script, name: "Run"));

        var targets = new[]
        {
            BuildTargetContext(1, "web-1", "web", CommunicationStyle.KubernetesApi),
            BuildTargetContext(2, "db-1", "db", CommunicationStyle.KubernetesApi)
        };

        var plan = await BuildPlanner().PlanAsync(BuildRequest(PlanMode.Preview, [step], targets), CancellationToken.None);

        var planned = plan.Steps.Single();
        planned.MatchedTargets.Count.ShouldBe(1);
        planned.MatchedTargets[0].MachineId.ShouldBe(1);
        planned.Dispatches.Count.ShouldBe(1);
    }

    [Fact]
    public async Task PlanAsync_NoTargetMatchesRequiredRoles_EmitsBlockerAndMarksStepNoMatchingTargets()
    {
        var step = BuildStep(id: 10, order: 1, name: "Deploy", roles: "payments");
        step.Actions.Add(BuildAction(id: 100, order: 1, actionType: SpecialVariables.ActionTypes.Script, name: "Run"));

        var targets = new[]
        {
            BuildTargetContext(1, "web-1", "web", CommunicationStyle.KubernetesApi),
            BuildTargetContext(2, "db-1", "db", CommunicationStyle.KubernetesApi)
        };

        var plan = await BuildPlanner().PlanAsync(BuildRequest(PlanMode.Preview, [step], targets), CancellationToken.None);

        plan.Steps.Single().Status.ShouldBe(PlannedStepStatus.NoMatchingTargets);

        var blocker = plan.BlockingReasons.ShouldHaveSingleItem();
        blocker.Code.ShouldBe(PlanBlockingReasonCodes.NoMatchingTargets);
        blocker.StepId.ShouldBe(10);
    }

    // ---------- no candidate targets at all -----------------------------

    [Fact]
    public async Task PlanAsync_TargetLevelStepButEmptyCandidatePool_EmitsNoSelectedMachinesBlocker()
    {
        var step = BuildStep(id: 10, order: 1, name: "Deploy", roles: "web");
        step.Actions.Add(BuildAction(id: 100, order: 1, actionType: SpecialVariables.ActionTypes.Script, name: "Run"));

        var plan = await BuildPlanner().PlanAsync(
            BuildRequest(PlanMode.Preview, [step], Array.Empty<DeploymentTargetContext>()),
            CancellationToken.None);

        plan.BlockingReasons
            .ShouldContain(r => r.Code == PlanBlockingReasonCodes.NoSelectedMachines);
    }

    // ---------- capability validation -----------------------------------

    [Fact]
    public async Task PlanAsync_TargetTransportRejectsActionType_EmitsCapabilityViolationBlocker()
    {
        var step = BuildStep(id: 10, order: 1, name: "Deploy");
        step.Actions.Add(BuildAction(id: 100, order: 1, actionType: "Squid.HelmChartUpgrade", name: "Upgrade"));

        var caps = new TransportCapabilities
        {
            SupportedActionTypes = TransportCapabilities.ActionTypes(SpecialVariables.ActionTypes.Script),
            SupportedSyntaxes = TransportCapabilities.Syntaxes(ScriptSyntax.Bash)
        };

        var targets = new[] { BuildTargetContext(1, "web-1", "web", CommunicationStyle.KubernetesApi, caps) };

        var plan = await BuildPlanner().PlanAsync(BuildRequest(PlanMode.Preview, [step], targets), CancellationToken.None);

        var planned = plan.Steps.Single();
        planned.Status.ShouldBe(PlannedStepStatus.Applicable);
        planned.Actions[0].Dispatches[0].Validation.IsValid.ShouldBeFalse();
        planned.Actions[0].Dispatches[0].Validation.Violations
            .ShouldContain(v => v.Code == ViolationCodes.UnsupportedActionType);

        plan.BlockingReasons
            .ShouldContain(r => r.Code == PlanBlockingReasonCodes.CapabilityViolation
                             && r.Detail == ViolationCodes.UnsupportedActionType);
    }

    [Fact]
    public async Task PlanAsync_TargetTransportMissing_EmitsTransportUnresolvedBlocker()
    {
        var step = BuildStep(id: 10, order: 1, name: "Deploy");
        step.Actions.Add(BuildAction(id: 100, order: 1, actionType: SpecialVariables.ActionTypes.Script, name: "Run"));

        var targetContext = new DeploymentTargetContext
        {
            Machine = new Machine { Id = 1, Name = "web-1", Roles = "[\"web\"]" },
            CommunicationStyle = CommunicationStyle.KubernetesApi,
            Transport = null
        };

        var plan = await BuildPlanner().PlanAsync(
            BuildRequest(PlanMode.Preview, [step], new[] { targetContext }),
            CancellationToken.None);

        plan.BlockingReasons
            .ShouldContain(r => r.Code == PlanBlockingReasonCodes.TransportUnresolved && r.MachineId == 1);
    }

    // ---------- Execute-mode throw path --------------------------------

    [Fact]
    public async Task PlanAsync_ExecuteModeWithBlockers_Throws()
    {
        var step = BuildStep(id: 10, order: 1, name: "Deploy", roles: "payments");
        step.Actions.Add(BuildAction(id: 100, order: 1, actionType: SpecialVariables.ActionTypes.Script, name: "Run"));

        var targets = new[] { BuildTargetContext(1, "web-1", "web", CommunicationStyle.KubernetesApi) };

        var ex = await Should.ThrowAsync<DeploymentPlanValidationException>(() =>
            BuildPlanner().PlanAsync(BuildRequest(PlanMode.Execute, [step], targets), CancellationToken.None));

        ex.Plan.Mode.ShouldBe(PlanMode.Execute);
        ex.Reasons.ShouldContain(r => r.Code == PlanBlockingReasonCodes.NoMatchingTargets);
    }

    [Fact]
    public async Task PlanAsync_PreviewModeWithBlockers_DoesNotThrow()
    {
        var step = BuildStep(id: 10, order: 1, name: "Deploy", roles: "payments");
        step.Actions.Add(BuildAction(id: 100, order: 1, actionType: SpecialVariables.ActionTypes.Script, name: "Run"));

        var targets = new[] { BuildTargetContext(1, "web-1", "web", CommunicationStyle.KubernetesApi) };

        var plan = await BuildPlanner().PlanAsync(BuildRequest(PlanMode.Preview, [step], targets), CancellationToken.None);

        plan.CanProceed.ShouldBeFalse();
        plan.BlockingReasons.ShouldNotBeEmpty();
    }

    // ---------- step ordering and multi-step ---------------------------

    [Fact]
    public async Task PlanAsync_MultipleSteps_AreReturnedInStepOrder()
    {
        var stepB = BuildStep(id: 20, order: 2, name: "Second");
        stepB.Actions.Add(BuildAction(id: 200, order: 1, actionType: SpecialVariables.ActionTypes.Script, name: "Run"));

        var stepA = BuildStep(id: 10, order: 1, name: "First");
        stepA.Actions.Add(BuildAction(id: 100, order: 1, actionType: SpecialVariables.ActionTypes.Script, name: "Run"));

        var targets = new[] { BuildTargetContext(1, "web-1", "web", CommunicationStyle.KubernetesApi) };

        var plan = await BuildPlanner().PlanAsync(
            BuildRequest(PlanMode.Preview, [stepB, stepA], targets),
            CancellationToken.None);

        plan.Steps.Select(s => s.StepId).ShouldBe(new[] { 10, 20 });
    }

    // ---------- candidate targets view ---------------------------------

    [Fact]
    public async Task PlanAsync_CandidateTargets_SortedByNameAndIncludeRoles()
    {
        var targets = new[]
        {
            BuildTargetContext(2, "zzz", "web", CommunicationStyle.KubernetesApi),
            BuildTargetContext(1, "aaa", "web,db", CommunicationStyle.KubernetesApi)
        };

        var step = BuildStep(id: 10, order: 1, name: "Deploy");
        step.Actions.Add(BuildAction(id: 100, order: 1, actionType: SpecialVariables.ActionTypes.Script, name: "Run"));

        var plan = await BuildPlanner().PlanAsync(BuildRequest(PlanMode.Preview, [step], targets), CancellationToken.None);

        plan.CandidateTargets.Select(t => t.MachineName).ShouldBe(new[] { "aaa", "zzz" });
        plan.CandidateTargets[0].Roles.ShouldBe(new[] { "db", "web" });
    }

    // ---------- helpers ------------------------------------------------

    private static DeploymentPlanRequest BuildRequest(
        PlanMode mode,
        IReadOnlyList<DeploymentStepDto> steps,
        IReadOnlyList<DeploymentTargetContext> targets) => new()
    {
        Mode = mode,
        ReleaseId = 1,
        EnvironmentId = 100,
        ChannelId = 200,
        DeploymentProcessSnapshotId = 999,
        Steps = steps,
        Variables = Array.Empty<VariableDto>(),
        TargetContexts = targets
    };

    private static DeploymentStepDto BuildStep(int id, int order, string name, string roles = null)
    {
        var step = new DeploymentStepDto
        {
            Id = id,
            StepOrder = order,
            Name = name,
            Properties = new List<DeploymentStepPropertyDto>(),
            Actions = new List<DeploymentActionDto>()
        };

        if (!string.IsNullOrEmpty(roles))
        {
            step.Properties.Add(new DeploymentStepPropertyDto
            {
                PropertyName = SpecialVariables.Step.TargetRoles,
                PropertyValue = roles
            });
        }

        return step;
    }

    private static DeploymentActionDto BuildAction(int id, int order, string actionType, string name) => new()
    {
        Id = id,
        ActionOrder = order,
        ActionType = actionType,
        Name = name
    };

    private static DeploymentTargetContext BuildTargetContext(
        int machineId,
        string machineName,
        string roles,
        CommunicationStyle style,
        ITransportCapabilities capabilities = null)
    {
        var caps = capabilities ?? new TransportCapabilities
        {
            SupportedSyntaxes = TransportCapabilities.Syntaxes(ScriptSyntax.Bash)
        };

        var transport = new Mock<IDeploymentTransport>();
        transport.SetupGet(t => t.CommunicationStyle).Returns(style);
        transport.SetupGet(t => t.Capabilities).Returns(caps);

        return new DeploymentTargetContext
        {
            Machine = new Machine
            {
                Id = machineId,
                Name = machineName,
                Roles = $"[{string.Join(",", roles.Split(',').Select(r => $"\"{r.Trim()}\""))}]"
            },
            CommunicationStyle = style,
            Transport = transport.Object
        };
    }
}
