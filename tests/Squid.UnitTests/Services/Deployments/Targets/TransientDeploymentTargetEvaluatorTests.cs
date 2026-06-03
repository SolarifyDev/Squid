using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.Deployments.Project;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Project;

namespace Squid.UnitTests.Services.Deployments.Targets;

/// <summary>
/// Exhaustive matrix for the project "Transient Deployment Targets" evaluator.
/// The default behaviours (SkipAndContinue + Exclude) MUST reproduce the historical
/// unconditional exclusion of unavailable + unhealthy targets, so the wiring is
/// non-breaking for unconfigured projects.
/// </summary>
public class TransientDeploymentTargetEvaluatorTests
{
    private static Machine M(string name, MachineHealthStatus status) => new() { Name = name, HealthStatus = status };

    private static TransientTargetResult Apply(IReadOnlyList<Machine> candidates,
        UnavailableDeploymentTargetBehavior unavailable = UnavailableDeploymentTargetBehavior.SkipAndContinue,
        UnhealthyDeploymentTargetBehavior unhealthy = UnhealthyDeploymentTargetBehavior.Exclude)
        => TransientDeploymentTargetEvaluator.Apply(candidates, unavailable, unhealthy);

    [Theory]
    [InlineData(MachineHealthStatus.Healthy)]
    [InlineData(MachineHealthStatus.Unknown)]
    [InlineData(MachineHealthStatus.HasWarnings)]
    public void Healthyish_AlwaysKept(MachineHealthStatus status)
    {
        var result = Apply(new[] { M("m", status) });

        result.Kept.Select(m => m.Name).ShouldBe(new[] { "m" });
        result.Skipped.ShouldBeEmpty();
        result.FailedUnavailable.ShouldBeEmpty();
    }

    [Fact]
    public void Defaults_ReproduceHistoricalExclusion()
    {
        // SkipAndContinue + Exclude (the defaults) reproduce the historical unconditional
        // exclusion: unavailable + unhealthy are removed (skipped), everything else kept,
        // nothing fails. This is the single health gate shared by deploy and preview.
        var machines = new[]
        {
            M("healthy", MachineHealthStatus.Healthy),
            M("unhealthy", MachineHealthStatus.Unhealthy),
            M("unknown", MachineHealthStatus.Unknown),
            M("unavailable", MachineHealthStatus.Unavailable),
            M("warnings", MachineHealthStatus.HasWarnings)
        };

        var result = Apply(machines);

        result.Kept.Select(m => m.Name).ShouldBe(new[] { "healthy", "unknown", "warnings" });
        result.Skipped.Select(m => m.Name).ShouldBe(new[] { "unhealthy", "unavailable" });
        result.FailedUnavailable.ShouldBeEmpty();
    }

    [Fact]
    public void Unavailable_FailDeployment_GoesToFailedList()
    {
        var result = Apply(new[] { M("down", MachineHealthStatus.Unavailable) },
            unavailable: UnavailableDeploymentTargetBehavior.FailDeployment);

        result.FailedUnavailable.Select(m => m.Name).ShouldBe(new[] { "down" });
        result.Kept.ShouldBeEmpty();
        result.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public void Unavailable_SkipAndContinue_GoesToSkipped()
    {
        var result = Apply(new[] { M("down", MachineHealthStatus.Unavailable) },
            unavailable: UnavailableDeploymentTargetBehavior.SkipAndContinue);

        result.Skipped.Select(m => m.Name).ShouldBe(new[] { "down" });
        result.FailedUnavailable.ShouldBeEmpty();
    }

    [Fact]
    public void Unhealthy_DoNotExclude_IsKept()
    {
        var result = Apply(new[] { M("sick", MachineHealthStatus.Unhealthy) },
            unhealthy: UnhealthyDeploymentTargetBehavior.DoNotExclude);

        result.Kept.Select(m => m.Name).ShouldBe(new[] { "sick" });
        result.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public void Unhealthy_Exclude_IsSkipped()
    {
        var result = Apply(new[] { M("sick", MachineHealthStatus.Unhealthy) },
            unhealthy: UnhealthyDeploymentTargetBehavior.Exclude);

        result.Skipped.Select(m => m.Name).ShouldBe(new[] { "sick" });
        result.Kept.ShouldBeEmpty();
    }

    [Fact]
    public void MaxParity_FailUnavailable_KeepUnhealthy_Combined()
    {
        // The non-default combination: fail on unavailable, keep unhealthy.
        var machines = new[]
        {
            M("healthy", MachineHealthStatus.Healthy),
            M("sick", MachineHealthStatus.Unhealthy),
            M("down", MachineHealthStatus.Unavailable)
        };

        var result = Apply(machines,
            unavailable: UnavailableDeploymentTargetBehavior.FailDeployment,
            unhealthy: UnhealthyDeploymentTargetBehavior.DoNotExclude);

        result.Kept.Select(m => m.Name).ShouldBe(new[] { "healthy", "sick" });
        result.FailedUnavailable.Select(m => m.Name).ShouldBe(new[] { "down" });
        result.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public void EmptyInput_AllListsEmpty()
    {
        var result = Apply(new List<Machine>());

        result.Kept.ShouldBeEmpty();
        result.Skipped.ShouldBeEmpty();
        result.FailedUnavailable.ShouldBeEmpty();
    }

    // ── ApplyProjectPolicy: the single entry point BOTH the deployment pipeline (phase 4)
    //    and the deployment preview call, so the two cannot disagree about eligibility. ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    public void ApplyProjectPolicy_NoOrInvalidSettings_UsesHistoricalDefaults(string settingsJson)
    {
        var machines = new[]
        {
            M("healthy", MachineHealthStatus.Healthy),
            M("unhealthy", MachineHealthStatus.Unhealthy),
            M("unavailable", MachineHealthStatus.Unavailable)
        };

        var result = TransientDeploymentTargetEvaluator.ApplyProjectPolicy(machines, settingsJson);

        result.Kept.Select(m => m.Name).ShouldBe(new[] { "healthy" });
        result.Skipped.Select(m => m.Name).ShouldBe(new[] { "unhealthy", "unavailable" });
        result.FailedUnavailable.ShouldBeEmpty();
    }

    [Fact]
    public void ApplyProjectPolicy_FailDeploymentSetting_FailsOnUnavailable()
    {
        var json = DeploymentSettingsSerializer.Serialize(new DeploymentSettingsDto
        {
            TransientDeploymentTargets = new TransientDeploymentTargetsDto
            {
                UnavailableDeploymentTargets = UnavailableDeploymentTargetBehavior.FailDeployment,
                UnhealthyDeploymentTargets = UnhealthyDeploymentTargetBehavior.Exclude
            }
        });

        var result = TransientDeploymentTargetEvaluator.ApplyProjectPolicy(new[] { M("down", MachineHealthStatus.Unavailable) }, json);

        result.FailedUnavailable.Select(m => m.Name).ShouldBe(new[] { "down" });
        result.Kept.ShouldBeEmpty();
    }

    [Fact]
    public void ApplyProjectPolicy_IsApplyWithPolicyFromJson_SoPreviewAndDeployConverge()
    {
        // Convergence guarantee: ApplyProjectPolicy == Apply with the policy resolved from the
        // project settings JSON. Preview and the deploy pipeline both route through it, so for
        // the same machines + settings they produce identical Kept / Skipped / Failed sets.
        var machines = new[]
        {
            M("healthy", MachineHealthStatus.Healthy),
            M("unhealthy", MachineHealthStatus.Unhealthy),
            M("unavailable", MachineHealthStatus.Unavailable)
        };
        var defaultsJson = DeploymentSettingsSerializer.Serialize(new DeploymentSettingsDto());

        var viaProjectPolicy = TransientDeploymentTargetEvaluator.ApplyProjectPolicy(machines, defaultsJson);
        var viaApply = Apply(machines);

        viaProjectPolicy.Kept.Select(m => m.Name).ShouldBe(viaApply.Kept.Select(m => m.Name));
        viaProjectPolicy.Skipped.Select(m => m.Name).ShouldBe(viaApply.Skipped.Select(m => m.Name));
        viaProjectPolicy.FailedUnavailable.Select(m => m.Name).ShouldBe(viaApply.FailedUnavailable.Select(m => m.Name));
    }
}
