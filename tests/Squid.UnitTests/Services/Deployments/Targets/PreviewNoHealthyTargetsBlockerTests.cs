using System.Collections.Generic;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Message.Models.Deployments.Deployment;

namespace Squid.UnitTests.Services.Deployments.Targets;

/// <summary>
/// The preview-side blocker that mirrors the deployment pipeline's "No target machines found"
/// failure: when a step genuinely needs targets but every matching target was excluded by the
/// transient-target health policy, the real deployment would fail — so the preview must block
/// (CanDeploy = false) rather than show the step as runnable.
/// </summary>
public class PreviewNoHealthyTargetsBlockerTests
{
    private static DeploymentPreviewStepResult NeedsTargets(int matched = 0) => new()
    {
        IsApplicable = true,
        IsRunOnServer = false,
        IsStepLevelOnly = false,
        RequiredRoles = ["web"],
        MatchedTargets = matched > 0 ? [new DeploymentPreviewTargetResult { MachineId = 1, MachineName = "ok" }] : [],
    };

    private static DeploymentPreviewTargetResult Excluded() => new() { MachineId = 9, MachineName = "down", HealthStatus = "Unavailable" };

    [Fact]
    public void StepNeedsTargets_AllExcludedByHealth_AddsBlocker()
    {
        var result = new DeploymentPreviewResult { Steps = [NeedsTargets()], ExcludedTargets = [Excluded()] };
        var blockers = new List<string>();

        DeploymentService.AddNoHealthyTargetsBlockerIfNeeded(result, blockers);

        blockers.Count.ShouldBe(1);
        blockers[0].ShouldContain("No healthy deployment targets");
    }

    [Fact]
    public void NoTargetsExcluded_DoesNotBlock()
    {
        // No health exclusion → a zero-target step is a role-mismatch concern, handled elsewhere.
        var result = new DeploymentPreviewResult { Steps = [NeedsTargets()], ExcludedTargets = [] };
        var blockers = new List<string>();

        DeploymentService.AddNoHealthyTargetsBlockerIfNeeded(result, blockers);

        blockers.ShouldBeEmpty();
    }

    [Fact]
    public void RunOnServerStep_DoesNotBlock_EvenWithExcludedTargets()
    {
        var result = new DeploymentPreviewResult
        {
            Steps = [new DeploymentPreviewStepResult { IsApplicable = true, IsRunOnServer = true, RequiredRoles = [] }],
            ExcludedTargets = [Excluded()],
        };
        var blockers = new List<string>();

        DeploymentService.AddNoHealthyTargetsBlockerIfNeeded(result, blockers);

        blockers.ShouldBeEmpty();
    }

    [Fact]
    public void StepStillHasHealthyTargets_DoesNotBlock()
    {
        var result = new DeploymentPreviewResult { Steps = [NeedsTargets(matched: 1)], ExcludedTargets = [Excluded()] };
        var blockers = new List<string>();

        DeploymentService.AddNoHealthyTargetsBlockerIfNeeded(result, blockers);

        blockers.ShouldBeEmpty();
    }
}
