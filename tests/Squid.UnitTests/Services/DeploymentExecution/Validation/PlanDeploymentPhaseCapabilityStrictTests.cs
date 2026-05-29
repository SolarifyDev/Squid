using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Planning;
using Squid.Core.Services.DeploymentExecution.Planning.Exceptions;
using Squid.Core.Services.DeploymentExecution.Validation;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.DeploymentExecution.Validation;

/// <summary>
/// Rule 11 behavioural matrix for strict capability enforcement at plan time.
/// strict fails the deployment PRE-FLIGHT on a capability mismatch; off/warn/
/// default never throw (they let the executor skip the dispatch). Scoped to
/// CapabilityViolation — other blockers (no matching targets) are not escalated.
/// Shares the serial env collection so the env-var override can't race.
/// </summary>
[Collection("CapabilityEnforcementEnv")]
public class PlanDeploymentPhaseCapabilityStrictTests
{
    [Fact]
    public Task Strict_WithCapabilityBlocker_ThrowsPreFlight()
        => WithEnv("strict", () =>
            Should.ThrowAsync<DeploymentPlanValidationException>(() => Run(PlanWith(PlanBlockingReasonCodes.CapabilityViolation))));

    [Theory]
    [InlineData(null)]    // unset → default warn
    [InlineData("warn")]
    [InlineData("off")]
    public Task NonStrict_WithCapabilityBlocker_DoesNotThrow(string mode)
        => WithEnv(mode, () =>
            Should.NotThrowAsync(() => Run(PlanWith(PlanBlockingReasonCodes.CapabilityViolation))));

    [Fact]
    public Task Strict_WithOnlyNonCapabilityBlocker_DoesNotThrow()
        // A no-matching-targets blocker is a separate concern from capability
        // suitability — strict capability enforcement must NOT escalate it.
        => WithEnv("strict", () =>
            Should.NotThrowAsync(() => Run(PlanWith(PlanBlockingReasonCodes.NoMatchingTargets))));

    private static Task Run(DeploymentPlan plan)
    {
        var planner = new Mock<IDeploymentPlanner>();
        planner.Setup(p => p.PlanAsync(It.IsAny<DeploymentPlanRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(plan);

        return new PlanDeploymentPhase(planner.Object).ExecuteAsync(BuildContext(), CancellationToken.None);
    }

    private static DeploymentPlan PlanWith(string blockerCode) => new()
    {
        Mode = PlanMode.Preview,
        ReleaseId = 1,
        EnvironmentId = 2,
        DeploymentProcessSnapshotId = 3,
        BlockingReasons = new[]
        {
            new PlanBlockingReason { Code = blockerCode, Message = "incompatible", MachineId = 1, MachineName = "linux-1" }
        }
    };

    private static DeploymentTaskContext BuildContext() => new()
    {
        ServerTaskId = 1,
        Deployment = new Deployment { Id = 1, EnvironmentId = 2, ChannelId = 3 },
        Release = new Squid.Core.Persistence.Entities.Deployments.Release { Id = 1, Version = "1.0.0" },
        ProcessSnapshot = new DeploymentProcessSnapshotDto { Id = 3 },
        Steps = new List<DeploymentStepDto> { new() { Id = 1, Name = "s", Properties = new(), Actions = new() } },
        Variables = new List<VariableDto>(),
        AllTargetsContext = new List<DeploymentTargetContext>()
    };

    private static async Task WithEnv(string value, Func<Task> body)
    {
        var original = System.Environment.GetEnvironmentVariable(CapabilityEnforcement.EnvVar);
        try
        {
            System.Environment.SetEnvironmentVariable(CapabilityEnforcement.EnvVar, value);
            await body();
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(CapabilityEnforcement.EnvVar, original);
        }
    }
}
