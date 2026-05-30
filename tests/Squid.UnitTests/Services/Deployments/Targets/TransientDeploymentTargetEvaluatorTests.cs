using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;

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
        // SkipAndContinue + Exclude (the defaults) == FilterByHealthStatus: unavailable
        // + unhealthy are removed (skipped), everything else kept, nothing fails.
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
}
