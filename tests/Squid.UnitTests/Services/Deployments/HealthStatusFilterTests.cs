using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Enums;

namespace Squid.UnitTests.Services.Deployments;

public class HealthStatusFilterTests
{
    [Theory]
    [InlineData(MachineHealthStatus.Unknown, true)]
    [InlineData(MachineHealthStatus.Healthy, true)]
    [InlineData(MachineHealthStatus.HasWarnings, true)]
    [InlineData(MachineHealthStatus.Unhealthy, false)]
    [InlineData(MachineHealthStatus.Unavailable, false)]
    public void FilterByHealthStatus_SingleMachine_IncludedBasedOnStatus(MachineHealthStatus status, bool expectedIncluded)
    {
        var machine = new Machine { Id = 1, Name = "m1", HealthStatus = status };

        var (healthy, excluded) = DeploymentTargetFinder.FilterByHealthStatus(new List<Machine> { machine });

        if (expectedIncluded)
        {
            healthy.ShouldContain(machine);
            excluded.ShouldNotContain(machine);
        }
        else
        {
            healthy.ShouldNotContain(machine);
            excluded.ShouldContain(machine);
        }
    }

    [Fact]
    public void FilterByHealthStatus_EmptyInput_ReturnsBothEmpty()
    {
        var (healthy, excluded) = DeploymentTargetFinder.FilterByHealthStatus(new List<Machine>());

        healthy.ShouldBeEmpty();
        excluded.ShouldBeEmpty();
    }

    [Fact]
    public void FilterByHealthStatus_MixedStatuses_CorrectPartitioning()
    {
        var machines = new List<Machine>
        {
            new() { Id = 1, Name = "healthy", HealthStatus = MachineHealthStatus.Healthy },
            new() { Id = 2, Name = "unhealthy", HealthStatus = MachineHealthStatus.Unhealthy },
            new() { Id = 3, Name = "unknown", HealthStatus = MachineHealthStatus.Unknown },
            new() { Id = 4, Name = "unavailable", HealthStatus = MachineHealthStatus.Unavailable },
            new() { Id = 5, Name = "warnings", HealthStatus = MachineHealthStatus.HasWarnings }
        };

        var (healthy, excluded) = DeploymentTargetFinder.FilterByHealthStatus(machines);

        healthy.Count.ShouldBe(3);
        excluded.Count.ShouldBe(2);
        healthy.Select(m => m.Name).ShouldBe(new[] { "healthy", "unknown", "warnings" });
        excluded.Select(m => m.Name).ShouldBe(new[] { "unhealthy", "unavailable" });
    }

    [Fact]
    public void FilterByHealthStatus_AllExcluded_HealthyEmpty()
    {
        var machines = new List<Machine>
        {
            new() { Id = 1, Name = "m1", HealthStatus = MachineHealthStatus.Unhealthy },
            new() { Id = 2, Name = "m2", HealthStatus = MachineHealthStatus.Unavailable }
        };

        var (healthy, excluded) = DeploymentTargetFinder.FilterByHealthStatus(machines);

        healthy.ShouldBeEmpty();
        excluded.Count.ShouldBe(2);
    }

    [Fact]
    public void FilterByHealthStatus_AllIncluded_ExcludedEmpty()
    {
        var machines = new List<Machine>
        {
            new() { Id = 1, Name = "m1", HealthStatus = MachineHealthStatus.Healthy },
            new() { Id = 2, Name = "m2", HealthStatus = MachineHealthStatus.Unknown }
        };

        var (healthy, excluded) = DeploymentTargetFinder.FilterByHealthStatus(machines);

        healthy.Count.ShouldBe(2);
        excluded.ShouldBeEmpty();
    }
}
