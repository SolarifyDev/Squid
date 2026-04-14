using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Planning;
using Squid.Core.Services.DeploymentExecution.Ssh;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Preview;

/// <summary>
/// Phase 6c-iii — verifies that capability violations detected by the planner surface
/// as <see cref="PlanBlockingReason"/> entries in the <see cref="DeploymentPlan"/>,
/// which the preview UI translates into <c>BlockingReasons</c> for the user.
/// </summary>
public class DeploymentPreviewCapabilityTests
{
    private readonly CapabilityValidator _validator = new();
    private readonly Mock<IActionHandlerRegistry> _registry = new();

    public DeploymentPreviewCapabilityTests()
    {
        _registry.Setup(r => r.ResolveScope(It.IsAny<DeploymentActionDto>()))
            .Returns(ExecutionScope.TargetLevel);
    }

    private DeploymentPlanner BuildPlanner() => new(_registry.Object, _validator);

    [Fact]
    public async Task Preview_SshTargetWithUnsupportedAction_BlockingReasonsContainViolation()
    {
        var step = BuildStep(10, "Deploy", roles: "web");
        step.Actions.Add(BuildAction(100, "Helm Upgrade", "Squid.HelmChartUpgrade"));

        var targets = new[]
        {
            BuildTargetContext(1, "ssh-web-1", "web", CommunicationStyle.Ssh, SshTransport.Capability)
        };

        var plan = await BuildPlanner().PlanAsync(BuildRequest(PlanMode.Preview, step, targets), CancellationToken.None);

        plan.CanProceed.ShouldBeFalse();
        plan.BlockingReasons.ShouldContain(r =>
            r.Code == PlanBlockingReasonCodes.CapabilityViolation
            && r.Detail == ViolationCodes.UnsupportedActionType
            && r.MachineId == 1);
    }

    [Fact]
    public async Task Preview_AllTargetsSupportAction_NoBlockingReasons()
    {
        var step = BuildStep(10, "Deploy", roles: "web");
        step.Actions.Add(BuildAction(100, "Run Script", SpecialVariables.ActionTypes.Script));

        var targets = new[]
        {
            BuildTargetContext(1, "k8s-web-1", "web", CommunicationStyle.KubernetesApi),
            BuildTargetContext(2, "ssh-web-1", "web", CommunicationStyle.Ssh, SshTransport.Capability)
        };

        var plan = await BuildPlanner().PlanAsync(BuildRequest(PlanMode.Preview, step, targets), CancellationToken.None);

        plan.CanProceed.ShouldBeTrue();
        plan.BlockingReasons.Where(r => r.Code == PlanBlockingReasonCodes.CapabilityViolation).ShouldBeEmpty();
    }

    // ---------- helpers --------------------------------------------------

    private static DeploymentPlanRequest BuildRequest(
        PlanMode mode,
        DeploymentStepDto step,
        IReadOnlyList<DeploymentTargetContext> targets) => new()
    {
        Mode = mode,
        ReleaseId = 1,
        EnvironmentId = 100,
        ChannelId = 200,
        DeploymentProcessSnapshotId = 999,
        Steps = new[] { step },
        Variables = Array.Empty<VariableDto>(),
        TargetContexts = targets
    };

    private static DeploymentStepDto BuildStep(int id, string name, string roles = null)
    {
        var step = new DeploymentStepDto
        {
            Id = id,
            StepOrder = 1,
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

    private static DeploymentActionDto BuildAction(int id, string name, string actionType) => new()
    {
        Id = id,
        ActionOrder = 1,
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
