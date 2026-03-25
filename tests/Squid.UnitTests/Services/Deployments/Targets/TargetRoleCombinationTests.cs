using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Models.Deployments.Process;
using Machine = Squid.Core.Persistence.Entities.Deployments.Machine;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.Deployments.Targets;

/// <summary>
/// Complex combination scenarios testing the full target role filtering pipeline:
/// multiple machines × multiple steps × different roles × conditions × disabled states.
/// Simulates realistic deployment routing where different steps target different machines.
/// </summary>
public class TargetRoleCombinationTests
{
    // ========== Multi-Machine × Multi-Step Routing ==========

    [Fact]
    public void ThreeMachines_ThreeSteps_EachTargetsDifferentRole()
    {
        // Setup: 3 machines with distinct roles, 3 steps each targeting one role
        var machines = new List<Machine>
        {
            MakeMachine(1, "web"),
            MakeMachine(2, "api"),
            MakeMachine(3, "database")
        };

        var steps = new List<DeploymentStepDto>
        {
            MakeStep(1, "Deploy Frontend", targetRoles: "web"),
            MakeStep(2, "Deploy API", targetRoles: "api"),
            MakeStep(3, "Run Migrations", targetRoles: "database")
        };

        // Machine 1 (web): only step 1 executes
        var m1Roles = ParseRoles(machines[0]);
        ShouldExecute(steps[0], m1Roles).ShouldBeTrue();
        ShouldExecute(steps[1], m1Roles).ShouldBeFalse();
        ShouldExecute(steps[2], m1Roles).ShouldBeFalse();

        // Machine 2 (api): only step 2 executes
        var m2Roles = ParseRoles(machines[1]);
        ShouldExecute(steps[0], m2Roles).ShouldBeFalse();
        ShouldExecute(steps[1], m2Roles).ShouldBeTrue();
        ShouldExecute(steps[2], m2Roles).ShouldBeFalse();

        // Machine 3 (database): only step 3 executes
        var m3Roles = ParseRoles(machines[2]);
        ShouldExecute(steps[0], m3Roles).ShouldBeFalse();
        ShouldExecute(steps[1], m3Roles).ShouldBeFalse();
        ShouldExecute(steps[2], m3Roles).ShouldBeTrue();
    }

    [Fact]
    public void MultiRoleMachine_ExecutesAllMatchingSteps()
    {
        // Machine has both "web" and "api" roles
        var machine = MakeMachine(1, "web", "api");
        var machineRoles = ParseRoles(machine);

        var steps = new List<DeploymentStepDto>
        {
            MakeStep(1, "Deploy Frontend", targetRoles: "web"),
            MakeStep(2, "Deploy API", targetRoles: "api"),
            MakeStep(3, "Run Migrations", targetRoles: "database")
        };

        ShouldExecute(steps[0], machineRoles).ShouldBeTrue();
        ShouldExecute(steps[1], machineRoles).ShouldBeTrue();
        ShouldExecute(steps[2], machineRoles).ShouldBeFalse();
    }

    [Fact]
    public void StepTargetsMultipleRoles_ExecutesOnAnyMatchingMachine()
    {
        // Step targets "web,api" — should run on machines with either role
        var step = MakeStep(1, "Health Check", targetRoles: "web,api");

        var webMachine = ParseRoles(MakeMachine(1, "web"));
        var apiMachine = ParseRoles(MakeMachine(2, "api"));
        var dbMachine = ParseRoles(MakeMachine(3, "database"));
        var multiMachine = ParseRoles(MakeMachine(4, "web", "api", "cache"));

        ShouldExecute(step, webMachine).ShouldBeTrue();
        ShouldExecute(step, apiMachine).ShouldBeTrue();
        ShouldExecute(step, dbMachine).ShouldBeFalse();
        ShouldExecute(step, multiMachine).ShouldBeTrue();
    }

    [Fact]
    public void StepWithNoRoles_ExecutesOnAllMachines()
    {
        // Step without target roles → runs on every machine
        var step = MakeStep(1, "Notify Slack");

        ShouldExecute(step, ParseRoles(MakeMachine(1, "web"))).ShouldBeTrue();
        ShouldExecute(step, ParseRoles(MakeMachine(2, "api"))).ShouldBeTrue();
        ShouldExecute(step, ParseRoles(MakeMachine(3, "database"))).ShouldBeTrue();
        ShouldExecute(step, ParseRoles(MakeMachine(4))).ShouldBeTrue();
    }

    // ========== Pre-Filtering (CollectAllTargetRoles + FilterByRoles) ==========

    [Fact]
    public void PreFilter_StepsTargetWebAndApi_OnlyRelevantMachinesSurvive()
    {
        var machines = new List<Machine>
        {
            MakeMachine(1, "web"),
            MakeMachine(2, "api"),
            MakeMachine(3, "database"),
            MakeMachine(4, "cache"),
            MakeMachine(5, "web", "api")
        };

        var steps = new List<DeploymentStepDto>
        {
            MakeStep(1, "Deploy Web", targetRoles: "web"),
            MakeStep(2, "Deploy API", targetRoles: "api")
        };

        var allRoles = DeploymentTargetFinder.CollectAllTargetRoles(steps);
        allRoles.Count.ShouldBe(2);

        var filtered = DeploymentTargetFinder.FilterByRoles(machines, allRoles);

        filtered.Count.ShouldBe(3); // machine 1 (web), 2 (api), 5 (web,api)
        filtered.ShouldContain(m => m.Id == 1);
        filtered.ShouldContain(m => m.Id == 2);
        filtered.ShouldContain(m => m.Id == 5);
        filtered.ShouldNotContain(m => m.Id == 3);
        filtered.ShouldNotContain(m => m.Id == 4);
    }

    [Fact]
    public void PreFilter_OneStepHasNoRoles_AllMachinesKept()
    {
        var machines = new List<Machine>
        {
            MakeMachine(1, "web"),
            MakeMachine(2, "api"),
            MakeMachine(3, "database")
        };

        var steps = new List<DeploymentStepDto>
        {
            MakeStep(1, "Deploy Web", targetRoles: "web"),
            MakeStep(2, "Notify All") // No target roles → needs all machines
        };

        var allRoles = DeploymentTargetFinder.CollectAllTargetRoles(steps);
        allRoles.ShouldBeEmpty(); // Empty = no pre-filtering

        // FilterByRoles with empty set returns all
        var filtered = DeploymentTargetFinder.FilterByRoles(machines, allRoles);
        filtered.Count.ShouldBe(3);
    }

    [Fact]
    public void PreFilter_DisabledStepRolesExcluded_OnlyEnabledStepRolesCollected()
    {
        var machines = new List<Machine>
        {
            MakeMachine(1, "web"),
            MakeMachine(2, "database")
        };

        var steps = new List<DeploymentStepDto>
        {
            MakeStep(1, "Deploy Web", targetRoles: "web"),
            MakeStep(2, "Run Migrations", targetRoles: "database", isDisabled: true)
        };

        var allRoles = DeploymentTargetFinder.CollectAllTargetRoles(steps);
        allRoles.Count.ShouldBe(1);
        allRoles.ShouldContain("web");

        var filtered = DeploymentTargetFinder.FilterByRoles(machines, allRoles);
        filtered.Count.ShouldBe(1);
        filtered[0].Id.ShouldBe(1);
    }

    // ========== Conditions + Roles Combined ==========

    [Fact]
    public void ConditionSuccess_RolesMatch_PreviousSucceeded_Executes()
    {
        var step = MakeStep(1, "Deploy", condition: "Success", targetRoles: "web");
        var machineRoles = ParseRoles(MakeMachine(1, "web"));

        StepEligibilityEvaluator.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ConditionSuccess_RolesMatch_PreviousFailed_Skips()
    {
        var step = MakeStep(1, "Deploy", condition: "Success", targetRoles: "web");
        var machineRoles = ParseRoles(MakeMachine(1, "web"));

        StepEligibilityEvaluator.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: false)
            .ShouldBeFalse();
    }

    [Fact]
    public void ConditionFailure_RolesMatch_PreviousFailed_Executes()
    {
        var step = MakeStep(1, "Rollback", condition: "Failure", targetRoles: "web");
        var machineRoles = ParseRoles(MakeMachine(1, "web"));

        StepEligibilityEvaluator.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: false)
            .ShouldBeTrue();
    }

    [Fact]
    public void ConditionAlways_RolesMismatch_Skips()
    {
        // Even "Always" condition doesn't override role mismatch
        var step = MakeStep(1, "Cleanup", condition: "Always", targetRoles: "web");
        var machineRoles = ParseRoles(MakeMachine(1, "database"));

        StepEligibilityEvaluator.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeFalse();
        StepEligibilityEvaluator.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: false)
            .ShouldBeFalse();
    }

    [Fact]
    public void Disabled_ConditionAlways_RolesMatch_StillSkips()
    {
        // Disabled overrides everything
        var step = MakeStep(1, "Deploy", condition: "Always", targetRoles: "web", isDisabled: true);
        var machineRoles = ParseRoles(MakeMachine(1, "web"));

        StepEligibilityEvaluator.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeFalse();
    }

    // ========== Realistic Multi-Step Pipeline Scenarios ==========

    [Fact]
    public void RealisticPipeline_WebApiDb_CorrectRouting()
    {
        // Realistic deployment: build step (no role) → web deploy → api deploy → db migration → smoke test (web,api)
        var steps = new List<DeploymentStepDto>
        {
            MakeStep(1, "Build Package"),
            MakeStep(2, "Deploy Frontend", targetRoles: "web-server"),
            MakeStep(3, "Deploy API", targetRoles: "api-server"),
            MakeStep(4, "Run DB Migration", targetRoles: "database"),
            MakeStep(5, "Smoke Test", targetRoles: "web-server,api-server")
        };

        var webServer = ParseRoles(MakeMachine(1, "web-server"));
        var apiServer = ParseRoles(MakeMachine(2, "api-server"));
        var dbServer = ParseRoles(MakeMachine(3, "database"));
        var fullStack = ParseRoles(MakeMachine(4, "web-server", "api-server", "database"));

        // web-server: steps 1 (no roles), 2, 5
        ShouldExecute(steps[0], webServer).ShouldBeTrue();
        ShouldExecute(steps[1], webServer).ShouldBeTrue();
        ShouldExecute(steps[2], webServer).ShouldBeFalse();
        ShouldExecute(steps[3], webServer).ShouldBeFalse();
        ShouldExecute(steps[4], webServer).ShouldBeTrue();

        // api-server: steps 1 (no roles), 3, 5
        ShouldExecute(steps[0], apiServer).ShouldBeTrue();
        ShouldExecute(steps[1], apiServer).ShouldBeFalse();
        ShouldExecute(steps[2], apiServer).ShouldBeTrue();
        ShouldExecute(steps[3], apiServer).ShouldBeFalse();
        ShouldExecute(steps[4], apiServer).ShouldBeTrue();

        // database: steps 1 (no roles), 4
        ShouldExecute(steps[0], dbServer).ShouldBeTrue();
        ShouldExecute(steps[1], dbServer).ShouldBeFalse();
        ShouldExecute(steps[2], dbServer).ShouldBeFalse();
        ShouldExecute(steps[3], dbServer).ShouldBeTrue();
        ShouldExecute(steps[4], dbServer).ShouldBeFalse();

        // full-stack: steps 1, 2, 3, 4, 5 (all)
        ShouldExecute(steps[0], fullStack).ShouldBeTrue();
        ShouldExecute(steps[1], fullStack).ShouldBeTrue();
        ShouldExecute(steps[2], fullStack).ShouldBeTrue();
        ShouldExecute(steps[3], fullStack).ShouldBeTrue();
        ShouldExecute(steps[4], fullStack).ShouldBeTrue();
    }

    [Fact]
    public void RealisticPipeline_PreFilterNarrowsMachines()
    {
        // Same realistic pipeline — pre-filtering should keep all 4 machines
        // because step 1 has no target roles
        var steps = new List<DeploymentStepDto>
        {
            MakeStep(1, "Build Package"),
            MakeStep(2, "Deploy Frontend", targetRoles: "web-server"),
            MakeStep(3, "Deploy API", targetRoles: "api-server")
        };

        var allRoles = DeploymentTargetFinder.CollectAllTargetRoles(steps);
        allRoles.ShouldBeEmpty(); // Step 1 has no roles → all machines needed
    }

    [Fact]
    public void RealisticPipeline_ManualInterventionStep_PreFilterStillNarrows()
    {
        // Manual Intervention step (StepLevel) has no roles but shouldn't block pre-filtering
        var machines = new List<Machine>
        {
            MakeMachine(1, "k8s-api-local"),
            MakeMachine(2, "k8s-agent-local")
        };

        var steps = new List<DeploymentStepDto>
        {
            MakeStepWithAction(1, "Manual Intervention", targetRoles: null, actionType: "Squid.ManualIntervention"),
            MakeStepWithAction(2, "Deploy web", targetRoles: "k8s-api-local", actionType: "Squid.KubernetesRunScript"),
            MakeStepWithAction(3, "Deploy web config", targetRoles: "k8s-api-local", actionType: "Squid.KubernetesRunScript")
        };

        Squid.Core.Services.DeploymentExecution.Handlers.ExecutionScope ScopeResolver(DeploymentActionDto a) =>
            a.ActionType == "Squid.ManualIntervention"
                ? Squid.Core.Services.DeploymentExecution.Handlers.ExecutionScope.StepLevel
                : Squid.Core.Services.DeploymentExecution.Handlers.ExecutionScope.TargetLevel;

        var allRoles = DeploymentTargetFinder.CollectAllTargetRoles(steps, ScopeResolver);
        allRoles.Count.ShouldBe(1);
        allRoles.ShouldContain("k8s-api-local");

        var filtered = DeploymentTargetFinder.FilterByRoles(machines, allRoles);
        filtered.Count.ShouldBe(1);
        filtered[0].Id.ShouldBe(1);
    }

    [Fact]
    public void RealisticPipeline_AllStepsHaveRoles_PreFilterWorks()
    {
        // Pipeline where every step has roles — pre-filtering can narrow
        var machines = new List<Machine>
        {
            MakeMachine(1, "web-server"),
            MakeMachine(2, "api-server"),
            MakeMachine(3, "database"),
            MakeMachine(4, "monitoring"), // Not needed by any step
            MakeMachine(5, "cache")       // Not needed by any step
        };

        var steps = new List<DeploymentStepDto>
        {
            MakeStep(1, "Deploy Frontend", targetRoles: "web-server"),
            MakeStep(2, "Deploy API", targetRoles: "api-server"),
            MakeStep(3, "Run DB Migration", targetRoles: "database")
        };

        var allRoles = DeploymentTargetFinder.CollectAllTargetRoles(steps);
        allRoles.Count.ShouldBe(3);

        var filtered = DeploymentTargetFinder.FilterByRoles(machines, allRoles);
        filtered.Count.ShouldBe(3);
        filtered.ShouldNotContain(m => m.Id == 4);
        filtered.ShouldNotContain(m => m.Id == 5);
    }

    // ========== K8s-Style Role Patterns ==========

    [Fact]
    public void K8sRoles_ClusterAndNamespace_CorrectMatching()
    {
        var prodCluster = ParseRoles(MakeMachine(1, "k8s-prod", "us-east-1"));
        var stagCluster = ParseRoles(MakeMachine(2, "k8s-staging", "us-west-2"));
        var devCluster = ParseRoles(MakeMachine(3, "k8s-dev", "eu-west-1"));

        var deployProd = MakeStep(1, "Deploy to Prod", targetRoles: "k8s-prod");
        var deployStaging = MakeStep(2, "Deploy to Staging", targetRoles: "k8s-staging");
        var deployAll = MakeStep(3, "Health Check All", targetRoles: "k8s-prod,k8s-staging,k8s-dev");

        ShouldExecute(deployProd, prodCluster).ShouldBeTrue();
        ShouldExecute(deployProd, stagCluster).ShouldBeFalse();
        ShouldExecute(deployProd, devCluster).ShouldBeFalse();

        ShouldExecute(deployStaging, stagCluster).ShouldBeTrue();

        ShouldExecute(deployAll, prodCluster).ShouldBeTrue();
        ShouldExecute(deployAll, stagCluster).ShouldBeTrue();
        ShouldExecute(deployAll, devCluster).ShouldBeTrue();
    }

    // ========== Action Environment + Channel Filtering Combined ==========

    [Fact]
    public void ActionFilter_EnvironmentChannelCombined_OnlyExactMatch()
    {
        var action = new DeploymentActionDto
        {
            Id = 1, StepId = 1, ActionOrder = 1, Name = "Deploy",
            ActionType = SpecialVariables.ActionTypes.Script, IsDisabled = false,
            Environments = new List<int> { 1, 2 },
            ExcludedEnvironments = new List<int> { 3 },
            Channels = new List<int> { 10, 20 }
        };

        // Both match
        StepEligibilityEvaluator.ShouldExecuteAction(action, 1, 10).ShouldBeTrue();
        StepEligibilityEvaluator.ShouldExecuteAction(action, 2, 20).ShouldBeTrue();

        // Environment match, wrong channel
        StepEligibilityEvaluator.ShouldExecuteAction(action, 1, 99).ShouldBeFalse();

        // Channel match, wrong environment
        StepEligibilityEvaluator.ShouldExecuteAction(action, 99, 10).ShouldBeFalse();

        // Excluded environment overrides inclusion
        StepEligibilityEvaluator.ShouldExecuteAction(action, 3, 10).ShouldBeFalse();
    }

    // ========== Edge Case: Role Name Overlap Prevention ==========

    [Fact]
    public void RoleNames_SimilarButDistinct_NoFalseMatching()
    {
        // These role names share prefixes/substrings but must match exactly
        var webMachine = ParseRoles(MakeMachine(1, "web"));
        var webServerMachine = ParseRoles(MakeMachine(2, "web-server"));
        var webWorkerMachine = ParseRoles(MakeMachine(3, "web-worker"));
        var webApiMachine = ParseRoles(MakeMachine(4, "web-api"));

        var step = MakeStep(1, "Deploy", targetRoles: "web");

        ShouldExecute(step, webMachine).ShouldBeTrue();
        ShouldExecute(step, webServerMachine).ShouldBeFalse();
        ShouldExecute(step, webWorkerMachine).ShouldBeFalse();
        ShouldExecute(step, webApiMachine).ShouldBeFalse();
    }

    [Fact]
    public void RoleNames_CaseMixed_MatchesCaseInsensitive()
    {
        var machine = ParseRoles(MakeMachine(1, "Web-Server", "API-Gateway"));

        var step1 = MakeStep(1, "Step1", targetRoles: "web-server");
        var step2 = MakeStep(2, "Step2", targetRoles: "API-GATEWAY");
        var step3 = MakeStep(3, "Step3", targetRoles: "api-gateway");
        var step4 = MakeStep(4, "Step4", targetRoles: "WEB-SERVER,api-gateway");

        ShouldExecute(step1, machine).ShouldBeTrue();
        ShouldExecute(step2, machine).ShouldBeTrue();
        ShouldExecute(step3, machine).ShouldBeTrue();
        ShouldExecute(step4, machine).ShouldBeTrue();
    }

    // ========== Helpers ==========

    private static bool ShouldExecute(DeploymentStepDto step, HashSet<string> machineRoles)
        => StepEligibilityEvaluator.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true);

    private static HashSet<string> ParseRoles(Machine machine)
        => DeploymentTargetFinder.ParseRoles(machine.Roles);

    private static Machine MakeMachine(int id, params string[] roles) => new()
    {
        Id = id,
        Name = $"Machine-{id}",
        IsDisabled = false,
        EnvironmentIds = "[1]",
        Roles = JsonSerializer.Serialize(roles),
        SpaceId = 1,
        Endpoint = "{}",
        Slug = $"machine-{id}"
    };

    private static DeploymentStepDto MakeStep(
        int order, string name,
        string targetRoles = null,
        string condition = "Success",
        bool isDisabled = false)
    {
        var step = new DeploymentStepDto
        {
            Id = order,
            StepOrder = order,
            Name = name,
            StepType = "Action",
            Condition = condition,
            IsDisabled = isDisabled,
            IsRequired = true,
            Properties = new List<DeploymentStepPropertyDto>()
        };

        if (targetRoles != null)
        {
            step.Properties.Add(new DeploymentStepPropertyDto
            {
                StepId = order,
                PropertyName = SpecialVariables.Step.TargetRoles,
                PropertyValue = targetRoles
            });
        }

        return step;
    }

    private static DeploymentStepDto MakeStepWithAction(int order, string name, string targetRoles, string actionType)
    {
        var step = MakeStep(order, name, targetRoles);
        step.Actions = new List<DeploymentActionDto>
        {
            new()
            {
                Id = order * 100,
                Name = name,
                ActionOrder = 1,
                ActionType = actionType,
                IsRequired = true,
                IsDisabled = false,
                Properties = new List<DeploymentActionPropertyDto>(),
                Environments = new List<int>(),
                ExcludedEnvironments = new List<int>(),
                Channels = new List<int>()
            }
        };

        return step;
    }

    // ========== Pre-Filter with Synthetic Steps (ResolveScope defaults) ==========

    [Fact]
    public void PreFilter_SyntheticStepNoHandler_StepLevelDefault_PreFilterStillNarrows()
    {
        // Simulates Acquire Packages: no registered handler → ResolveScope returns StepLevel
        // This role-less synthetic step should NOT prevent pre-filtering
        var machines = new List<Machine>
        {
            MakeMachine(1, "web"),
            MakeMachine(2, "api"),
            MakeMachine(3, "database")
        };

        var steps = new List<DeploymentStepDto>
        {
            MakeStepWithAction(1, "Acquire Packages", targetRoles: null, actionType: "Squid.TentaclePackage"),
            MakeStepWithAction(2, "Deploy Web", targetRoles: "web", actionType: "Squid.KubernetesRunScript")
        };

        // No handler for TentaclePackage → StepLevel (default for unknown)
        // KubernetesRunScript → TargetLevel (known handler)
        ExecutionScope ScopeResolver(DeploymentActionDto a) =>
            a.ActionType == "Squid.KubernetesRunScript"
                ? ExecutionScope.TargetLevel
                : ExecutionScope.StepLevel;

        var allRoles = DeploymentTargetFinder.CollectAllTargetRoles(steps, ScopeResolver);
        allRoles.Count.ShouldBe(1);
        allRoles.ShouldContain("web");

        var filtered = DeploymentTargetFinder.FilterByRoles(machines, allRoles);
        filtered.Count.ShouldBe(1);
        filtered[0].Id.ShouldBe(1);
    }
}
