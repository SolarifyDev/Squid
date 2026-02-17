using System;
using System.Collections.Generic;
using Squid.Core.Services.Deployments;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments;

/// <summary>
/// Tests for static pipeline filter methods on DeploymentTaskExecutor:
///   - ShouldExecuteStep: IsDisabled check, Condition evaluation, Role matching
///   - ShouldExecuteAction: IsDisabled check, Environment/Channel filtering
/// Complex combination scenarios (condition + roles + disabled) are in TargetRoleCombinationTests.
/// </summary>
public class DeploymentPipelineFilterTests
{
    // ========== IsDisabled Gate ==========

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ShouldExecuteStep_IsDisabled(bool isDisabled, bool expected)
    {
        var step = MakeStep(isDisabled: isDisabled);

        DeploymentTaskExecutor.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: true)
            .ShouldBe(expected);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ShouldExecuteAction_IsDisabled(bool isDisabled, bool expected)
    {
        var action = MakeAction(isDisabled: isDisabled);

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 1)
            .ShouldBe(expected);
    }

    // ========== Condition Evaluation ==========

    [Theory]
    [InlineData("Success", true, true)]
    [InlineData("Success", false, false)]
    [InlineData("Failure", false, true)]
    [InlineData("Failure", true, false)]
    [InlineData("Always", true, true)]
    [InlineData("Always", false, true)]
    [InlineData("Variable", true, true)]    // Variable treated as Always (not yet supported)
    [InlineData("Variable", false, true)]
    [InlineData(null, true, true)]           // null treated as Success
    [InlineData(null, false, false)]
    [InlineData("", true, true)]             // empty treated as Success
    [InlineData("", false, false)]
    public void ShouldExecuteStep_ConditionEvaluation(string condition, bool previousSucceeded, bool expected)
    {
        var step = MakeStep(condition: condition);

        DeploymentTaskExecutor.ShouldExecuteStep(step, targetRoles: null, previousStepSucceeded: previousSucceeded)
            .ShouldBe(expected);
    }

    // ========== Per-Step Role Matching ==========

    [Theory]
    [InlineData("web", "web", true)]                                             // exact single-role match
    [InlineData("web,api", "web", true)]                                         // multi-step-role, single machine match
    [InlineData("web,api", "database", false)]                                   // no match
    [InlineData("Web-Server", "web-server", true)]                               // case insensitive
    [InlineData("web,api,worker", "api", true)]                                  // OR logic — any match suffices
    [InlineData("web-server", "web", false)]                                     // substring — no false positive
    [InlineData("web", "web-server", false)]                                     // reverse substring — no false positive
    [InlineData("k8s-worker.us-east-1", "k8s-worker.us-east-1", true)]          // special characters
    [InlineData("database", "web,api,database,cache", true)]                     // machine multi-role, one matches
    [InlineData("web,api,worker", "database,api,cache", true)]                   // both multi-role, overlap on "api"
    [InlineData("web,worker", "database,api,cache", false)]                      // both multi-role, no overlap
    [InlineData(" web , api ", "web", true)]                                     // whitespace trimmed
    public void ShouldExecuteStep_RoleMatching(string stepRoles, string machineRolesStr, bool expected)
    {
        var step = MakeStep(targetRoles: stepRoles);
        var machineRoles = new HashSet<string>(
            machineRolesStr.Split(',', StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBe(expected);
    }

    [Fact]
    public void ShouldExecuteStep_NoTargetRoles_ExecutesOnAll()
    {
        var step = MakeStep(targetRoles: null);
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "anything" };

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_EmptyTargetRoles_ExecutesOnAll()
    {
        var step = MakeStep(targetRoles: "");

        DeploymentTaskExecutor.ShouldExecuteStep(step, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "any" }, previousStepSucceeded: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteStep_EmptyMachineRoles_StepRequiresRoles_ReturnsFalse()
    {
        var step = MakeStep(targetRoles: "web");
        var machineRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        DeploymentTaskExecutor.ShouldExecuteStep(step, machineRoles, previousStepSucceeded: true)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldExecuteStep_NullProperties_ExecutesOnAll()
    {
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
    public void ShouldExecuteStep_OtherProperties_DontInterfereWithRoleMatch()
    {
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

    // ========== Per-Action Environment Filtering ==========

    [Fact]
    public void ShouldExecuteAction_NoEnvironments_ExecutesInAll()
    {
        var action = MakeAction(environments: new List<int>());

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 99, deploymentChannelId: 1)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteAction_EnvironmentMatches_ReturnsTrue()
    {
        var action = MakeAction(environments: new List<int> { 1, 2, 3 });

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 2, deploymentChannelId: 1)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteAction_EnvironmentNoMatch_ReturnsFalse()
    {
        var action = MakeAction(environments: new List<int> { 1, 2 });

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 99, deploymentChannelId: 1)
            .ShouldBeFalse();
    }

    // ========== Per-Action Channel Filtering ==========

    [Fact]
    public void ShouldExecuteAction_NoChannels_ExecutesInAll()
    {
        var action = MakeAction(channels: new List<int>());

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 99)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteAction_ChannelMatches_ReturnsTrue()
    {
        var action = MakeAction(channels: new List<int> { 10, 20 });

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 10)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteAction_ChannelNoMatch_ReturnsFalse()
    {
        var action = MakeAction(channels: new List<int> { 10, 20 });

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 99)
            .ShouldBeFalse();
    }

    // ========== ExcludedEnvironments ==========

    [Fact]
    public void ShouldExecuteAction_ExcludedEnvironment_ReturnsFalse()
    {
        var action = MakeAction(excludedEnvironments: new List<int> { 5, 10 });

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 5, deploymentChannelId: 1)
            .ShouldBeFalse();
    }

    [Fact]
    public void ShouldExecuteAction_NotExcluded_ReturnsTrue()
    {
        var action = MakeAction(excludedEnvironments: new List<int> { 5, 10 });

        DeploymentTaskExecutor.ShouldExecuteAction(action, deploymentEnvironmentId: 1, deploymentChannelId: 1)
            .ShouldBeTrue();
    }

    [Fact]
    public void ShouldExecuteAction_InclusionAndExclusion_ExclusionWins()
    {
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
