using System;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums.Deployments;
using Squid.Core.Services.Deployments.LifeCycle;

namespace Squid.UnitTests.Services.Deployments.Lifecycle;

public class LifecycleProgressionEvaluatorTests
{
    // ─── Single-Phase Scenarios ───

    [Fact]
    public void SinglePhase_NoDeployments_AllEnvsAllowed()
    {
        var phases = new List<LifecyclePhase>
        {
            MakePhase(1, "Dev", 0)
        };
        var envs = new List<LifecyclePhaseEnvironment>
        {
            MakeAutoEnv(1, 10),
            MakeAutoEnv(1, 20)
        };
        var deployed = new HashSet<int>();

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);

        result.CurrentPhaseIndex.ShouldBe(0);
        result.AllowedEnvironmentIds.ShouldContain(10);
        result.AllowedEnvironmentIds.ShouldContain(20);
        result.AutoDeployEnvironmentIds.ShouldContain(10);
        result.AutoDeployEnvironmentIds.ShouldContain(20);
        result.Phases[0].IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void SinglePhase_AllDeployed_PhaseComplete()
    {
        var phases = new List<LifecyclePhase>
        {
            MakePhase(1, "Dev", 0)
        };
        var envs = new List<LifecyclePhaseEnvironment>
        {
            MakeAutoEnv(1, 10),
            MakeAutoEnv(1, 20)
        };
        var deployed = new HashSet<int> { 10, 20 };

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);

        result.Phases[0].IsComplete.ShouldBeTrue();
        result.CurrentPhaseIndex.ShouldBe(1);
    }

    // ─── Two-Phase Promotion Scenarios ───

    [Fact]
    public void TwoPhases_FirstComplete_SecondPhaseAllowed()
    {
        var phases = new List<LifecyclePhase>
        {
            MakePhase(1, "Dev", 0),
            MakePhase(2, "Staging", 1)
        };
        var envs = new List<LifecyclePhaseEnvironment>
        {
            MakeAutoEnv(1, 10),
            MakeAutoEnv(2, 20)
        };
        var deployed = new HashSet<int> { 10 };

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);

        result.CurrentPhaseIndex.ShouldBe(1);
        result.AllowedEnvironmentIds.ShouldContain(10);
        result.AllowedEnvironmentIds.ShouldContain(20);
        result.Phases[0].IsComplete.ShouldBeTrue();
        result.Phases[1].IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void TwoPhases_FirstIncomplete_SecondPhaseNotAllowed()
    {
        var phases = new List<LifecyclePhase>
        {
            MakePhase(1, "Dev", 0),
            MakePhase(2, "Staging", 1)
        };
        var envs = new List<LifecyclePhaseEnvironment>
        {
            MakeAutoEnv(1, 10),
            MakeAutoEnv(1, 11),
            MakeAutoEnv(2, 20)
        };
        // Only deployed to env 10, but env 11 still missing
        var deployed = new HashSet<int> { 10 };

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);

        result.CurrentPhaseIndex.ShouldBe(0);
        result.AllowedEnvironmentIds.ShouldContain(10);
        result.AllowedEnvironmentIds.ShouldContain(11);
        result.AllowedEnvironmentIds.ShouldNotContain(20);
        result.Phases[0].IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void ThreePhases_DevStagingProd_ProgressivePromotion()
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
        var deployed = new HashSet<int> { 10, 20 };

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);

        result.CurrentPhaseIndex.ShouldBe(2);
        result.AllowedEnvironmentIds.ShouldContain(10);
        result.AllowedEnvironmentIds.ShouldContain(20);
        result.AllowedEnvironmentIds.ShouldContain(30);
        result.Phases[0].IsComplete.ShouldBeTrue();
        result.Phases[1].IsComplete.ShouldBeTrue();
        result.Phases[2].IsComplete.ShouldBeFalse();
    }

    // ─── Optional Phase ───

    [Fact]
    public void OptionalPhase_AlwaysComplete_AllowsPromotion()
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
        // Only deployed to Dev — UAT is optional so should be skippable
        var deployed = new HashSet<int> { 10 };

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);

        result.Phases[1].IsComplete.ShouldBeTrue();
        result.Phases[1].IsOptional.ShouldBeTrue();
        result.AllowedEnvironmentIds.ShouldContain(30);
    }

    // ─── MinimumEnvironmentsBeforePromotion ───

    [Theory]
    [InlineData(1, true)]   // Need 1 deployed, have 1 → complete
    [InlineData(2, true)]   // Need 2 deployed, have 2 → complete
    [InlineData(3, false)]  // Need 3 deployed, have 2 → incomplete
    public void MinimumEnvironments_ThresholdEnforcement(int minimum, bool expectedComplete)
    {
        var phases = new List<LifecyclePhase>
        {
            MakePhase(1, "Dev", 0, minimumEnvs: minimum),
            MakePhase(2, "Staging", 1)
        };
        var envs = new List<LifecyclePhaseEnvironment>
        {
            MakeAutoEnv(1, 10),
            MakeAutoEnv(1, 11),
            MakeAutoEnv(1, 12),
            MakeAutoEnv(2, 20)
        };
        var deployed = new HashSet<int> { 10, 11 };

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);

        result.Phases[0].IsComplete.ShouldBe(expectedComplete);
    }

    // ─── Empty Phase (Inherits Prior) ───

    [Fact]
    public void EmptyPhase_InheritsPriorEnvironments()
    {
        var phases = new List<LifecyclePhase>
        {
            MakePhase(1, "Dev", 0),
            MakePhase(2, "Inherited", 1)  // no environments
        };
        var envs = new List<LifecyclePhaseEnvironment>
        {
            MakeAutoEnv(1, 10)
        };
        // Phase 2 has no environments but inherits env 10 from phase 1
        var deployed = new HashSet<int> { 10 };

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);

        result.Phases[0].IsComplete.ShouldBeTrue();
        result.Phases[1].IsComplete.ShouldBeTrue();
    }

    // ─── Auto-Deploy Environment IDs ───

    [Fact]
    public void AutoDeployIds_OnlyFromNextIncompletePhase()
    {
        var phases = new List<LifecyclePhase>
        {
            MakePhase(1, "Dev", 0),
            MakePhase(2, "Staging", 1)
        };
        var envs = new List<LifecyclePhaseEnvironment>
        {
            MakeAutoEnv(1, 10),
            MakeAutoEnv(2, 20),
            MakeOptionalEnv(2, 21)
        };
        var deployed = new HashSet<int> { 10 };

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);

        result.AutoDeployEnvironmentIds.ShouldContain(20);
        result.AutoDeployEnvironmentIds.ShouldNotContain(21); // optional, not auto
    }

    // ─── Mixed Automatic and Optional Targets ───

    [Fact]
    public void MixedTargets_OnlyAutomaticRequired_ForCompletion()
    {
        var phases = new List<LifecyclePhase>
        {
            MakePhase(1, "Dev", 0),
            MakePhase(2, "Prod", 1)
        };
        var envs = new List<LifecyclePhaseEnvironment>
        {
            MakeAutoEnv(1, 10),
            MakeOptionalEnv(1, 11),  // optional — not required for completion
            MakeAutoEnv(2, 20)
        };
        // Deployed to 10 only (not 11 which is optional)
        var deployed = new HashSet<int> { 10 };

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);

        result.Phases[0].IsComplete.ShouldBeTrue();
        result.AllowedEnvironmentIds.ShouldContain(20);
    }

    // ─── No Phases ───

    [Fact]
    public void NoPhases_EmptyResult()
    {
        var result = LifecycleProgressionEvaluator.EvaluatePhases(
            new List<LifecyclePhase>(), new List<LifecyclePhaseEnvironment>(), new HashSet<int>());

        result.CurrentPhaseIndex.ShouldBe(0);
        result.AllowedEnvironmentIds.ShouldBeEmpty();
        result.AutoDeployEnvironmentIds.ShouldBeEmpty();
        result.Phases.ShouldBeEmpty();
    }

    // ─── Phase Status Details ───

    [Fact]
    public void PhaseStatus_TracksDeployedEnvironments()
    {
        var phases = new List<LifecyclePhase>
        {
            MakePhase(1, "Dev", 0)
        };
        var envs = new List<LifecyclePhaseEnvironment>
        {
            MakeAutoEnv(1, 10),
            MakeAutoEnv(1, 20),
            MakeOptionalEnv(1, 30)
        };
        var deployed = new HashSet<int> { 10, 30 };

        var result = LifecycleProgressionEvaluator.EvaluatePhases(phases, envs, deployed);

        var status = result.Phases[0];
        status.AutomaticEnvironmentIds.ShouldBe(new List<int> { 10, 20 });
        status.OptionalEnvironmentIds.ShouldBe(new List<int> { 30 });
        status.DeployedEnvironmentIds.ShouldContain(10);
        status.DeployedEnvironmentIds.ShouldContain(30);
        status.DeployedEnvironmentIds.ShouldNotContain(20);
        status.IsComplete.ShouldBeFalse(); // env 20 (automatic) not deployed
    }

    // ─── Helpers ───

    private static LifecyclePhase MakePhase(int id, string name, int sortOrder, int minimumEnvs = 0, bool isOptional = false)
    {
        return new LifecyclePhase
        {
            Id = id,
            Name = name,
            SortOrder = sortOrder,
            MinimumEnvironmentsBeforePromotion = minimumEnvs,
            IsOptionalPhase = isOptional,
            LifecycleId = 1
        };
    }

    private static LifecyclePhaseEnvironment MakeAutoEnv(int phaseId, int environmentId)
    {
        return new LifecyclePhaseEnvironment
        {
            PhaseId = phaseId,
            EnvironmentId = environmentId,
            TargetType = LifecyclePhaseEnvironmentTargetType.Automatic
        };
    }

    private static LifecyclePhaseEnvironment MakeOptionalEnv(int phaseId, int environmentId)
    {
        return new LifecyclePhaseEnvironment
        {
            PhaseId = phaseId,
            EnvironmentId = environmentId,
            TargetType = LifecyclePhaseEnvironmentTargetType.Optional
        };
    }
}
