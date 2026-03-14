using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.Release;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Release;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;

namespace Squid.UnitTests.Services.Deployments.Lifecycle;

public class ReleaseLifecycleProgressionTests
{
    // ─── Progress Field ───

    [Theory]
    [InlineData(true, 0, 1, "Complete")]
    [InlineData(false, 0, 0, "Current")]
    [InlineData(false, 1, 0, "Pending")]
    public void PhaseProgress_MappedCorrectly(bool isComplete, int phaseIndex, int currentPhaseIndex, string expectedProgress)
    {
        var phases = CreatePhasesForProgressTest(isComplete, phaseIndex, currentPhaseIndex);
        var envs = new List<LifecyclePhaseEnvironment> { MakeAutoEnv(1, 10), MakeAutoEnv(2, 20) };
        var deployed = isComplete ? new HashSet<int> { 10 } : new HashSet<int>();

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);
        var dto = BuildDto(result);

        dto.Phases[phaseIndex].Progress.ShouldBe(expectedProgress);
    }

    // ─── CanDeploy ───

    [Fact]
    public void CanDeploy_OnlyTrueForAllowedEnvironments()
    {
        var phases = new List<LifecyclePhase>
        {
            MakePhase(1, "Dev", 0),
            MakePhase(2, "Prod", 1)
        };
        var envs = new List<LifecyclePhaseEnvironment>
        {
            MakeAutoEnv(1, 10),
            MakeAutoEnv(2, 20)
        };
        var deployed = new HashSet<int>();

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);
        var dto = BuildDto(result);

        dto.Phases[0].Environments[0].CanDeploy.ShouldBeTrue();
        dto.Phases[1].Environments[0].CanDeploy.ShouldBeFalse();
    }

    [Fact]
    public void CanDeploy_AfterFirstPhaseComplete_SecondPhaseAllowed()
    {
        var phases = new List<LifecyclePhase>
        {
            MakePhase(1, "Dev", 0),
            MakePhase(2, "Prod", 1)
        };
        var envs = new List<LifecyclePhaseEnvironment>
        {
            MakeAutoEnv(1, 10),
            MakeAutoEnv(2, 20)
        };
        var deployed = new HashSet<int> { 10 };

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);
        var dto = BuildDto(result);

        dto.Phases[0].Environments[0].CanDeploy.ShouldBeTrue();
        dto.Phases[1].Environments[0].CanDeploy.ShouldBeTrue();
    }

    // ─── IsAutomatic ───

    [Fact]
    public void IsAutomatic_MappedFromPhaseEnvironmentTargetType()
    {
        var phases = new List<LifecyclePhase> { MakePhase(1, "Dev", 0) };
        var envs = new List<LifecyclePhaseEnvironment>
        {
            MakeAutoEnv(1, 10),
            MakeOptionalEnv(1, 20)
        };
        var deployed = new HashSet<int>();

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);
        var dto = BuildDto(result);

        var phase = dto.Phases[0];
        phase.Environments.Single(e => e.EnvironmentId == 10).IsAutomatic.ShouldBeTrue();
        phase.Environments.Single(e => e.EnvironmentId == 20).IsAutomatic.ShouldBeFalse();
    }

    // ─── Deployment Info ───

    [Fact]
    public void DeploymentInfo_NullWhenNoDeployment()
    {
        var phases = new List<LifecyclePhase> { MakePhase(1, "Dev", 0) };
        var envs = new List<LifecyclePhaseEnvironment> { MakeAutoEnv(1, 10) };
        var deployed = new HashSet<int>();

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);
        var dto = BuildDto(result);

        dto.Phases[0].Environments[0].Deployment.ShouldBeNull();
    }

    [Fact]
    public void DeploymentInfo_PopulatedWhenDeploymentExists()
    {
        var phases = new List<LifecyclePhase> { MakePhase(1, "Dev", 0) };
        var envs = new List<LifecyclePhaseEnvironment> { MakeAutoEnv(1, 10) };
        var deployed = new HashSet<int> { 10 };

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);

        var created = DateTimeOffset.UtcNow.AddMinutes(-5);
        var completed = DateTimeOffset.UtcNow;
        var latestDeployments = new Dictionary<int, (int DeploymentId, string State, DateTimeOffset Created, DateTimeOffset? CompletedTime)>
        {
            [10] = (42, "Success", created, completed)
        };

        var dto = BuildDto(result, latestDeployments: latestDeployments);

        var deployment = dto.Phases[0].Environments[0].Deployment;
        deployment.ShouldNotBeNull();
        deployment.DeploymentId.ShouldBe(42);
        deployment.State.ShouldBe("Success");
        deployment.CreatedDate.ShouldBe(created);
        deployment.CompletedTime.ShouldBe(completed);
    }

    // ─── Three-Phase Full Scenario ───

    [Fact]
    public void ThreePhases_PartiallyDeployed_CorrectProgressAndCanDeploy()
    {
        var phases = new List<LifecyclePhase>
        {
            MakePhase(1, "Dev", 0),
            MakePhase(2, "Staging", 1),
            MakePhase(3, "Production", 2)
        };
        var envs = new List<LifecyclePhaseEnvironment>
        {
            MakeAutoEnv(1, 10),
            MakeAutoEnv(2, 20),
            MakeAutoEnv(3, 30)
        };
        var deployed = new HashSet<int> { 10 };

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);
        var dto = BuildDto(result);

        dto.Phases[0].Progress.ShouldBe("Complete");
        dto.Phases[0].IsComplete.ShouldBeTrue();
        dto.Phases[0].Environments[0].CanDeploy.ShouldBeTrue();

        dto.Phases[1].Progress.ShouldBe("Current");
        dto.Phases[1].IsComplete.ShouldBeFalse();
        dto.Phases[1].Environments[0].CanDeploy.ShouldBeTrue();

        dto.Phases[2].Progress.ShouldBe("Pending");
        dto.Phases[2].IsComplete.ShouldBeFalse();
        dto.Phases[2].Environments[0].CanDeploy.ShouldBeFalse();
    }

    // ─── Optional Phase ───

    [Fact]
    public void OptionalPhase_IsOptionalTrue_AlwaysComplete()
    {
        var phases = new List<LifecyclePhase>
        {
            MakePhase(1, "Dev", 0),
            MakePhase(2, "UAT", 1, isOptional: true),
            MakePhase(3, "Prod", 2)
        };
        var envs = new List<LifecyclePhaseEnvironment>
        {
            MakeAutoEnv(1, 10),
            MakeAutoEnv(2, 20),
            MakeAutoEnv(3, 30)
        };
        var deployed = new HashSet<int> { 10 };

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);
        var dto = BuildDto(result);

        dto.Phases[1].IsOptional.ShouldBeTrue();
        dto.Phases[1].IsComplete.ShouldBeTrue();
        dto.Phases[1].Progress.ShouldBe("Complete");
        dto.Phases[2].Environments[0].CanDeploy.ShouldBeTrue();
    }

    // ─── Environment Name Mapping ───

    [Fact]
    public void EnvironmentName_MappedFromLookup()
    {
        var phases = new List<LifecyclePhase> { MakePhase(1, "Dev", 0) };
        var envs = new List<LifecyclePhaseEnvironment> { MakeAutoEnv(1, 10) };
        var deployed = new HashSet<int>();

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);
        var names = new Dictionary<int, string> { [10] = "Development" };
        var dto = BuildDto(result, names);

        dto.Phases[0].Environments[0].EnvironmentName.ShouldBe("Development");
    }

    // ─── Helpers ───

    private static ReleaseLifecycleProgressionDto BuildDto(
        PhaseProgressionResult progression,
        Dictionary<int, string> environmentNames = null,
        Dictionary<int, (int DeploymentId, string State, DateTimeOffset Created, DateTimeOffset? CompletedTime)> latestDeployments = null)
    {
        var release = new ReleaseEntity { Id = 1, Version = "1.0.0" };
        var lifecycle = new Squid.Core.Persistence.Entities.Deployments.Lifecycle { Id = 1, Name = "Default Lifecycle" };

        return ReleaseService.AssembleProgressionDto(
            release, lifecycle, progression,
            environmentNames ?? new Dictionary<int, string>(),
            latestDeployments ?? new Dictionary<int, (int, string, DateTimeOffset, DateTimeOffset?)>());
    }

    private static List<LifecyclePhase> CreatePhasesForProgressTest(bool isComplete, int phaseIndex, int currentPhaseIndex)
    {
        var phases = new List<LifecyclePhase>();

        for (var i = 0; i <= Math.Max(phaseIndex, currentPhaseIndex); i++)
        {
            phases.Add(MakePhase(i + 1, $"Phase{i}", i));
        }

        return phases;
    }

    private static LifecyclePhase MakePhase(int id, string name, int sortOrder, bool isOptional = false)
    {
        return new LifecyclePhase
        {
            Id = id, Name = name, SortOrder = sortOrder,
            IsOptionalPhase = isOptional, LifecycleId = 1
        };
    }

    private static LifecyclePhaseEnvironment MakeAutoEnv(int phaseId, int environmentId)
    {
        return new LifecyclePhaseEnvironment
        {
            PhaseId = phaseId, EnvironmentId = environmentId,
            TargetType = LifecyclePhaseEnvironmentTargetType.Automatic
        };
    }

    private static LifecyclePhaseEnvironment MakeOptionalEnv(int phaseId, int environmentId)
    {
        return new LifecyclePhaseEnvironment
        {
            PhaseId = phaseId, EnvironmentId = environmentId,
            TargetType = LifecyclePhaseEnvironmentTargetType.Optional
        };
    }
}
