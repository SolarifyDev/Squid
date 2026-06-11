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
/// The defaults for an unconfigured project are <see cref="UnavailableDeploymentTargetBehavior.FailDeployment"/>
/// (fail-fast on an unreachable target rather than silently skipping it and reporting success)
/// and <see cref="UnhealthyDeploymentTargetBehavior.Exclude"/>. Explicit SkipAndContinue /
/// DoNotExclude opt back into the lenient behaviour.
/// </summary>
public class TransientDeploymentTargetEvaluatorTests
{
    private static Machine M(string name, MachineHealthStatus status) => new() { Name = name, HealthStatus = status };

    // The helper's default policy mirrors the production DTO default so `Apply(machines)`
    // reflects what an unconfigured project gets.
    private static TransientTargetResult Apply(IReadOnlyList<Machine> candidates,
        UnavailableDeploymentTargetBehavior unavailable = UnavailableDeploymentTargetBehavior.FailDeployment,
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
    public void Defaults_FailUnavailable_ExcludeUnhealthy()
    {
        // Fail-fast defaults: an unavailable target fails the deployment; unhealthy is excluded
        // (skipped); healthy / unknown / warnings proceed. This is the single health gate shared
        // by deploy and preview.
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
        result.Skipped.Select(m => m.Name).ShouldBe(new[] { "unhealthy" });
        result.FailedUnavailable.Select(m => m.Name).ShouldBe(new[] { "unavailable" });
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
    public void ApplyProjectPolicy_NoOrInvalidSettings_FailUnavailable_ExcludeUnhealthy(string settingsJson)
    {
        var machines = new[]
        {
            M("healthy", MachineHealthStatus.Healthy),
            M("unhealthy", MachineHealthStatus.Unhealthy),
            M("unavailable", MachineHealthStatus.Unavailable)
        };

        var result = TransientDeploymentTargetEvaluator.ApplyProjectPolicy(machines, settingsJson);

        result.Kept.Select(m => m.Name).ShouldBe(new[] { "healthy" });
        result.Skipped.Select(m => m.Name).ShouldBe(new[] { "unhealthy" });
        result.FailedUnavailable.Select(m => m.Name).ShouldBe(new[] { "unavailable" });
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

    // ── Role scoping: the role-aware overload narrows the candidate set to the
    //    deployment's step roles BEFORE applying the policy, so an unavailable target
    //    that no step targets cannot fail the deployment. Both the pipeline (phase 4)
    //    and the preview call THIS overload, so the role scoping is shared too. ──

    private static Machine Role(Machine m, params string[] roles)
    {
        m.Roles = DeploymentTargetFinder.SerializeRoles(roles);
        return m;
    }

    [Fact]
    public void ApplyProjectPolicy_RoleScoped_IgnoresUnavailableTargetWithUnmatchedRole()
    {
        // The reported bug: an unavailable target whose role no step targets must NOT
        // appear in FailedUnavailable — it is not a deployment target for this release.
        var web = Role(M("web-01", MachineHealthStatus.Healthy), "web");
        var dbDown = Role(M("db-down", MachineHealthStatus.Unavailable), "db");
        var defaultsJson = DeploymentSettingsSerializer.Serialize(new DeploymentSettingsDto());

        var result = TransientDeploymentTargetEvaluator.ApplyProjectPolicy(new[] { web, dbDown }, ["web"], defaultsJson);

        result.FailedUnavailable.ShouldBeEmpty();
        result.Kept.Select(m => m.Name).ShouldBe(new[] { "web-01" });
    }

    [Fact]
    public void ApplyProjectPolicy_RoleScoped_StillFailsOnUnavailableMatchedRole()
    {
        // Role scoping NARROWS the set; it does not disable the policy. An unavailable
        // target that IS a deployment target (matches a step role) still fails.
        var webDown = Role(M("web-down", MachineHealthStatus.Unavailable), "web");
        var defaultsJson = DeploymentSettingsSerializer.Serialize(new DeploymentSettingsDto());

        var result = TransientDeploymentTargetEvaluator.ApplyProjectPolicy(new[] { webDown }, ["web"], defaultsJson);

        result.FailedUnavailable.Select(m => m.Name).ShouldBe(new[] { "web-down" });
    }

    [Fact]
    public void ApplyProjectPolicy_RoleScoped_EmptyRoles_EvaluatesAllTargets_LikeUnscoped()
    {
        // A step targeting all machines (no roles) → no narrowing → identical to the
        // role-unaware overload, so existing role-less processes behave exactly as before.
        var down = Role(M("down", MachineHealthStatus.Unavailable), "db");
        var defaultsJson = DeploymentSettingsSerializer.Serialize(new DeploymentSettingsDto());

        var scoped = TransientDeploymentTargetEvaluator.ApplyProjectPolicy(new[] { down }, [], defaultsJson);
        var unscoped = TransientDeploymentTargetEvaluator.ApplyProjectPolicy(new[] { down }, defaultsJson);

        scoped.FailedUnavailable.Select(m => m.Name).ShouldBe(unscoped.FailedUnavailable.Select(m => m.Name));
        scoped.FailedUnavailable.Select(m => m.Name).ShouldBe(new[] { "down" });
    }
}
