using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.Project;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Project;
using ProjectEntity = Squid.Core.Persistence.Entities.Deployments.Project;

namespace Squid.UnitTests.Services.Deployments.Targets;

/// <summary>
/// Pins how phase 4 applies the project "Transient Deployment Targets" setting to the
/// candidate targets: default (no settings) fails fast on an unavailable target and still
/// excludes unhealthy ones; explicit SkipAndContinue skips an unavailable target; DoNotExclude
/// keeps unhealthy targets.
/// </summary>
public class PrepareDeploymentPhaseTransientTargetTests
{
    private static Machine M(string name, MachineHealthStatus status) => new() { Name = name, HealthStatus = status };

    private static DeploymentTaskContext Context(string deploymentSettingsJson, params Machine[] targets) => new()
    {
        Deployment = new Deployment { Id = 1 },
        Project = new ProjectEntity { DeploymentSettingsJson = deploymentSettingsJson },
        AllTargets = targets.ToList()
    };

    private static string Json(UnavailableDeploymentTargetBehavior unavailable, UnhealthyDeploymentTargetBehavior unhealthy)
        => DeploymentSettingsSerializer.Serialize(new DeploymentSettingsDto
        {
            TransientDeploymentTargets = new TransientDeploymentTargetsDto
            {
                UnavailableDeploymentTargets = unavailable,
                UnhealthyDeploymentTargets = unhealthy
            }
        });

    [Fact]
    public void Default_NullSettings_FailsOnUnavailable()
    {
        // New fail-fast default: an unconfigured project with an unavailable target aborts
        // the deployment up front instead of silently skipping it and reporting success.
        var ctx = Context(null,
            M("healthy", MachineHealthStatus.Healthy),
            M("unavailable", MachineHealthStatus.Unavailable));

        Should.Throw<DeploymentTargetException>(() => PrepareDeploymentPhase.ApplyTransientTargetPolicy(ctx))
            .Message.ShouldContain("unavailable");
    }

    [Fact]
    public void Default_NullSettings_NoUnavailable_ExcludesUnhealthy()
    {
        // The unhealthy default (Exclude) is unchanged: with no unavailable target present,
        // unhealthy targets are still excluded and the deployment proceeds on the rest.
        var ctx = Context(null,
            M("healthy", MachineHealthStatus.Healthy),
            M("unhealthy", MachineHealthStatus.Unhealthy));

        PrepareDeploymentPhase.ApplyTransientTargetPolicy(ctx);

        ctx.AllTargets.Select(m => m.Name).ShouldBe(new[] { "healthy" });
        ctx.ExcludedByHealthTargets.Select(m => m.Name).ShouldBe(new[] { "unhealthy" });
    }

    [Fact]
    public void FailDeployment_UnavailableTarget_Throws()
    {
        var ctx = Context(
            Json(UnavailableDeploymentTargetBehavior.FailDeployment, UnhealthyDeploymentTargetBehavior.Exclude),
            M("healthy", MachineHealthStatus.Healthy),
            M("down", MachineHealthStatus.Unavailable));

        Should.Throw<DeploymentTargetException>(() => PrepareDeploymentPhase.ApplyTransientTargetPolicy(ctx))
            .Message.ShouldContain("down");
    }

    [Fact]
    public void DoNotExclude_UnhealthyTarget_IsKept()
    {
        var ctx = Context(
            Json(UnavailableDeploymentTargetBehavior.SkipAndContinue, UnhealthyDeploymentTargetBehavior.DoNotExclude),
            M("healthy", MachineHealthStatus.Healthy),
            M("sick", MachineHealthStatus.Unhealthy));

        PrepareDeploymentPhase.ApplyTransientTargetPolicy(ctx);

        ctx.AllTargets.Select(m => m.Name).ShouldBe(new[] { "healthy", "sick" });
    }

    [Fact]
    public void SkipAndContinue_UnavailableTarget_IsSkipped()
    {
        var ctx = Context(
            Json(UnavailableDeploymentTargetBehavior.SkipAndContinue, UnhealthyDeploymentTargetBehavior.Exclude),
            M("healthy", MachineHealthStatus.Healthy),
            M("down", MachineHealthStatus.Unavailable));

        PrepareDeploymentPhase.ApplyTransientTargetPolicy(ctx);

        ctx.AllTargets.Select(m => m.Name).ShouldBe(new[] { "healthy" });
        ctx.ExcludedByHealthTargets.Select(m => m.Name).ShouldBe(new[] { "down" });
    }
}
