using System;
using System.Collections.Generic;
using Squid.Core.Services.Deployments;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments;

/// <summary>
/// Tests for static pipeline filter methods on DeploymentTaskExecutor:
///   - ShouldExecuteStep: IsDisabled check, Condition evaluation, Role matching
///   - ShouldExecuteAction: IsDisabled check, Environment/Channel filtering
/// </summary>
public class DeploymentPipelineFilterTests
{
    // ========== Fix A: IsDisabled skip ==========

    [Fact]
    public void ShouldExecuteStep_DisabledStep_ReturnsFalse()
    {
        var step = MakeStep(isDisabled: true);

        DeploymentTaskExecutor.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: true)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldExecuteStep_EnabledStep_ReturnsTrue()
    {
        var step = MakeStep(isDisabled: false);

        DeploymentTaskExecutor.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteAction_DisabledAction_ReturnsFalse()
    {
        var action = MakeAction(isDisabled: true);

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 1)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldExecuteAction_EnabledAction_ReturnsTrue()
    {
        var action = MakeAction(isDisabled: false);

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 1)
            .ShouldBeTrue();
    }

    // ========== Fix C: Step Condition evaluation ==========

    [Fact]
    public void ShouldExecuteStep_ConditionSuccess_PreviousSucceeded_ReturnsTrue()
    {
        var step = MakeStep(condition: "Success");

        DeploymentTaskExecutor.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_ConditionSuccess_PreviousFailed_ReturnsFalse()
    {
        var step = MakeStep(condition: "Success");

        DeploymentTaskExecutor.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: false)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldExecuteStep_ConditionFailure_PreviousFailed_ReturnsTrue()
    {
        var step = MakeStep(condition: "Failure");

        DeploymentTaskExecutor.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: false)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_ConditionFailure_PreviousSucceeded_ReturnsFalse()
    {
        var step = MakeStep(condition: "Failure");

        DeploymentTaskExecutor.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: true)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldExecuteStep_ConditionAlways_PreviousFailed_ReturnsTrue()
    {
        var step = MakeStep(condition: "Always");

        DeploymentTaskExecutor.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: false)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_ConditionAlways_PreviousSucceeded_ReturnsTrue()
    {
        var step = MakeStep(condition: "Always");

        DeploymentTaskExecutor.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_ConditionVariable_TreatedAsAlways()
    {
        // Variable conditions require expression evaluation — not yet supported.
        // Default: treat as Always (execute).
        var step = MakeStep(condition: "Variable");

        DeploymentTaskExecutor.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: true)
            .ShouldBeTrue();
        DeploymentTaskExecutor.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: false)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_NullOrEmptyCondition_TreatedAsSuccess()
    {
        var step = MakeStep(condition: null);

        DeploymentTaskExecutor.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: true)
            .ShouldBeTrue();
        DeploymentTaskExecutor.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: false)
            .ShouldBeFalse();
    }

    // ========== Fix D: Per-step role filtering ==========

    [Fact]
    public void ShouldExecuteStep_TargetHasMatchingRole_ReturnsTrue()
    {
        var step = MakeStep(targetRoles: "web,api");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_TargetHasNoMatchingRole_ReturnsFalse()
    {
        var step = MakeStep(targetRoles: "web,api");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "database" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldExecuteStep_StepHasNoTargetRoles_ExecutesOnAllTargets()
    {
        var step = MakeStep(targetRoles: null);
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "anything" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_CaseInsensitiveRoleMatching()
    {
        var step = MakeStep(targetRoles: "Web-Server");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web-server" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    // ========== Fix E: Per-action environment/channel filtering ==========

    [Fact]
    public void ShouldExecuteAction_ActionHasNoEnvironments_ExecutesInAllEnvironments()
    {
        var action = MakeAction(environments: new List<int>());

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 99, deploymentChannelId: 1)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteAction_ActionEnvironmentMatches_ReturnsTrue()
    {
        var action = MakeAction(environments: new List<int> { 1, 2, 3 });

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 2, deploymentChannelId: 1)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteAction_ActionEnvironmentDoesNotMatch_ReturnsFalse()
    {
        var action = MakeAction(environments: new List<int> { 1, 2 });

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 99, deploymentChannelId: 1)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldExecuteAction_ActionHasNoChannels_ExecutesInAllChannels()
    {
        var action = MakeAction(channels: new List<int>());

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 99)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteAction_ActionChannelMatches_ReturnsTrue()
    {
        var action = MakeAction(channels: new List<int> { 10, 20 });

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 10)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteAction_ActionChannelDoesNotMatch_ReturnsFalse()
    {
        var action = MakeAction(channels: new List<int> { 10, 20 });

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 99)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldExecuteAction_BothEnvironmentAndChannelMustMatch()
    {
        var action = MakeAction(
            environments: new List<int> { 1 },
            channels: new List<int> { 10 });

        // Both match
        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 10)
            .ShouldBeTrue();

        // Environment matches, channel doesn't
        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 99)
            .ShouldBeFalse();

        // Channel matches, environment doesn't
        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 99, deploymentChannelId: 10)
            .ShouldBeFalse();
    }

    // ========== Fix E+: ExcludedEnvironments filtering ==========

    [Fact]
    public void ShouldExecuteAction_ExcludedEnvironment_ReturnsFalse()
    {
        var action = MakeAction(excludedEnvironments: new List<int> { 5, 10 });

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 5, deploymentChannelId: 1)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldExecuteAction_NotInExcludedEnvironments_ReturnsTrue()
    {
        var action = MakeAction(excludedEnvironments: new List<int> { 5, 10 });

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 1)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteAction_BothInclusionAndExclusion_ExclusionWins()
    {
        // Environment 5 is in both inclusion and exclusion lists — exclusion takes precedence
        var action = MakeAction(
            environments: new List<int> { 1, 5 },
            excludedEnvironments: new List<int> { 5 });

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 5, deploymentChannelId: 1)
            .ShouldBeFalse();

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 1)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteAction_OnlyExclusionList_AllowsNonExcluded()
    {
        var action = MakeAction(excludedEnvironments: new List<int> { 99 });

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 1)
            .ShouldBeTrue();

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 99, deploymentChannelId: 1)
            .ShouldBeFalse();
    }

    // ========== Phase 4: Test Hardening ==========

    [Fact]
    public void DisabledOverridesAlwaysCondition()
    {
        var step = MakeStep(isDisabled: true, condition: "Always");

        DeploymentTaskExecutor.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: true)
            .ShouldBeFalse();
        DeploymentTaskExecutor.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: false)
            .ShouldBeFalse();
    }

    [Fact]
    public void ConditionFailure_WithMatchingRoles_PreviousFailed()
    {
        var step = MakeStep(condition: "Failure", targetRoles: "web");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: false)
            .ShouldBeTrue();
    }

    [Fact]
    public void ConditionSuccess_WithNonMatchingRoles()
    {
        var step = MakeStep(condition: "Success", targetRoles: "web");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "database" };

        // Role mismatch short-circuits even though condition would pass
        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeFalse();
    }

    // ========== Target Role Edge Cases (Octopus Alignment) ==========

    [Fact]
    public void ShouldExecuteStep_MultipleTargetRoles_AnyMatchSuffices()
    {
        // Octopus OR logic: machine needs ANY of the step's target roles
        var step = MakeStep(targetRoles: "web,api,worker");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_MachineHasEmptyRoles_StepRequiresRoles_ReturnsFalse()
    {
        // Machine with no roles cannot execute a step that requires specific roles
        var step = MakeStep(targetRoles: "web");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldExecuteStep_RoleSubstringDoesNotFalseMatch()
    {
        // "web" must NOT match "web-server" — exact role matching, not substring
        var step = MakeStep(targetRoles: "web-server");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldExecuteStep_RoleSubstringReverse_DoesNotFalseMatch()
    {
        // "web-server" must NOT match step role "web"
        var step = MakeStep(targetRoles: "web");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web-server" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldExecuteStep_RolesWithSpecialCharacters_MatchCorrectly()
    {
        // Roles can contain dashes, dots, underscores
        var step = MakeStep(targetRoles: "k8s-worker.us-east-1");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "k8s-worker.us-east-1" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_EmptyStringTargetRolesValue_ExecutesOnAll()
    {
        // Step has the property but empty value → no filter, executes on all machines
        var step = MakeStep(targetRoles: "");

        DeploymentTaskExecutor.ShouldExecuteStep(step, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "any" }, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_DisabledWithMatchingRoles_ReturnsFalse()
    {
        // Disabled takes precedence over role match
        var step = MakeStep(isDisabled: true, targetRoles: "web");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldExecuteStep_ConditionFailsButRolesMatch_ReturnsFalse()
    {
        // Condition check runs before role check — both must pass
        var step = MakeStep(condition: "Success", targetRoles: "web");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: false)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldExecuteStep_AllThreeChecksPass_ReturnsTrue()
    {
        // Enabled + condition passes + roles match → execute
        var step = MakeStep(isDisabled: false, condition: "Always", targetRoles: "web,api");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: false)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_MultiplePropertiesOnStep_OnlyTargetRolesChecked()
    {
        // Other properties don't interfere with role matching
        var step = MakeStep(targetRoles: "web");
        step.Properties.Add(new DeploymentStepPropertyDto
        {
            StepId = 1, PropertyName = "Octopus.Action.MaxParallelism", PropertyValue = "5"
        });
        step.Properties.Add(new DeploymentStepPropertyDto
        {
            StepId = 1, PropertyName = "Octopus.Action.RunOnServer", PropertyValue = "false"
        });

        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_NullStepProperties_ReturnsTrue()
    {
        // Null Properties list → no role filter → execute on all
        var step = new DeploymentStepDto
        {
            Id = 1, StepOrder = 1, Name = "Test Step", StepType = "Action",
            Condition = "Success", IsDisabled = false, IsRequired = true,
            Properties = null
        };

        DeploymentTaskExecutor.ShouldExecuteStep(step, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" }, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_MachineHasMultipleRoles_OneMatches_ReturnsTrue()
    {
        // Machine has many roles, step only requires one — OR logic
        var step = MakeStep(targetRoles: "database");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web", "api", "database", "cache" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_BothStepAndMachineHaveMultipleRoles_OverlapExists()
    {
        // Multiple roles on both sides, overlap on "api"
        var step = MakeStep(targetRoles: "web,api,worker");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "database", "api", "cache" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_BothStepAndMachineHaveMultipleRoles_NoOverlap()
    {
        var step = MakeStep(targetRoles: "web,worker");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "database", "api", "cache" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldExecuteStep_WhitespaceInTargetRoles_TrimmedCorrectly()
    {
        // Whitespace around role names should be trimmed
        var step = MakeStep(targetRoles: " web , api ");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    // ========== Helpers ==========

    private static DeploymentStepDto MakeStep(
        bool isDisabled = false,
        string condition = "Success",
        string targetRoles = null)
    {
        var step = new DeploymentStepDto
        {
            Id = 1,
            StepOrder = 1,
            Name = "Test Step",
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
                StepId = 1,
                PropertyName = DeploymentVariables.Action.TargetRoles,
                PropertyValue = targetRoles
            });
        }

        return step;
    }

    private static DeploymentActionDto MakeAction(
        bool isDisabled = false,
        List<int> environments = null,
        List<int> excludedEnvironments = null,
        List<int> channels = null)
    {
        return new DeploymentActionDto
        {
            Id = 1,
            StepId = 1,
            ActionOrder = 1,
            Name = "Test Action",
            ActionType = "Octopus.Script",
            IsDisabled = isDisabled,
            Environments = environments ?? new List<int>(),
            ExcludedEnvironments = excludedEnvironments ?? new List<int>(),
            Channels = channels ?? new List<int>()
        };
    }
}
