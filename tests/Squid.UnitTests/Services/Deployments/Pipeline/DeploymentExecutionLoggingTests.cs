using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Message.Constants;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

/// <summary>
/// Acceptance tests for deployment execution logging: verifies log messages content,
/// category (Info/Warning/Error), and scope (correct activityNodeId).
/// </summary>
public class DeploymentExecutionLoggingTests
{
    private record CapturedLog(ServerTaskLogCategory Category, string Message, string Source, long? ActivityNodeId);

    // ========== Step Skip Logging ==========

    [Fact]
    public async Task StepSkip_Disabled_LogsUnderStepNode()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness();

        var step = MakeStep("Deploy disabled", 1, "web", isDisabled: true);
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull("Step node should be created even when skipping");

        var skipLog = logs.FirstOrDefault(l => l.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase));
        skipLog.ShouldNotBeNull("Should log a disabled skip message");
        skipLog.ActivityNodeId.ShouldBe(stepNode.Id);
    }

    [Fact]
    public async Task StepSkip_SuccessCondition_LogsUnderStepNode()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness();
        ctx.FailureEncountered = true;

        var step = MakeStep("Deploy on success", 1, "web", condition: "Success");
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull();

        var skipLog = logs.FirstOrDefault(l => l.Message.Contains("previous step failed", StringComparison.OrdinalIgnoreCase));
        skipLog.ShouldNotBeNull("Should log that a previous step failed");
        skipLog.ActivityNodeId.ShouldBe(stepNode.Id);
    }

    [Fact]
    public async Task StepSkip_FailureCondition_LogsUnderStepNode()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness();
        ctx.FailureEncountered = false;

        var step = MakeStep("Rollback on failure", 1, "web", condition: "Failure");
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull();

        var skipLog = logs.FirstOrDefault(l => l.Message.Contains("no previous step has failed", StringComparison.OrdinalIgnoreCase));
        skipLog.ShouldNotBeNull("Should log that no previous step has failed");
        skipLog.ActivityNodeId.ShouldBe(stepNode.Id);
    }

    [Fact]
    public async Task StepSkip_RoleMismatch_LogsUnderStepNode()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness(machineRoles: "web");

        var step = MakeStep("Deploy database", 1, "database");
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull();

        var skipLog = logs.FirstOrDefault(l => l.Message.Contains("no machines were found", StringComparison.OrdinalIgnoreCase));
        skipLog.ShouldNotBeNull("Should log no machines found for role mismatch");
        skipLog.ActivityNodeId.ShouldBe(stepNode.Id);
        skipLog.Category.ShouldBe(ServerTaskLogCategory.Warning);
    }

    // ========== Step Execution Logging ==========

    [Fact]
    public async Task StepExecutes_LogsUnderStepNode()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness();

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("RunScript") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull();

        var execLog = logs.FirstOrDefault(l => l.Message.Contains("Executing step", StringComparison.OrdinalIgnoreCase));
        execLog.ShouldNotBeNull("Should log step execution start");
        execLog.ActivityNodeId.ShouldBe(stepNode.Id);
    }

    // ========== All Actions Filtered — Step Invisible ==========

    [Fact]
    public async Task AllActionsDisabled_StepIsInvisible()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness();

        var action = MakeAction("RunScript");
        action.IsDisabled = true;
        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { action };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        nodes.ShouldNotContain(n => n.NodeType == DeploymentActivityLogNodeType.Step, "Step node should not be created when all actions filtered");
        logs.ShouldBeEmpty();
    }

    [Fact]
    public async Task AllActionsEnvironmentMismatch_StepIsInvisible()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness();

        var action = MakeAction("RunScript");
        action.Environments = new List<int> { 99 };
        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { action };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        nodes.ShouldNotContain(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        logs.ShouldBeEmpty();
    }

    [Fact]
    public async Task AllActionsChannelMismatch_StepIsInvisible()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness();

        var action = MakeAction("RunScript");
        action.Channels = new List<int> { 99 };
        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { action };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        nodes.ShouldNotContain(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        logs.ShouldBeEmpty();
    }

    [Fact]
    public async Task AllActionsManuallySkipped_StepIsInvisible()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness();

        var action = MakeAction("RunScript");
        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { action };
        ctx.Steps = new List<DeploymentStepDto> { step };
        ctx.Deployment.DeploymentRequestPayload = new DeploymentRequestPayload
        {
            SkipActionIds = new List<int> { action.Id }
        };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        nodes.ShouldNotContain(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        logs.ShouldBeEmpty();
    }

    // ========== Partial Action Skip — Step Still Executes ==========

    [Fact]
    public async Task PartialActionSkip_EnvironmentMismatch_LogsSkipForFilteredAction()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness();

        var filteredAction = MakeAction("FilteredAction");
        filteredAction.Environments = new List<int> { 99 };
        var eligibleAction = MakeAction("RunScript");
        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { filteredAction, eligibleAction };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull("Step node should exist when some actions are eligible");

        var skipLog = logs.FirstOrDefault(l => l.Message.Contains("environment", StringComparison.OrdinalIgnoreCase) && l.Message.Contains("FilteredAction", StringComparison.OrdinalIgnoreCase));
        skipLog.ShouldNotBeNull("Should log skip for filtered action");
        skipLog.Category.ShouldBe(ServerTaskLogCategory.Warning);
    }

    // ========== Action Execution Logging ==========

    [Fact]
    public async Task ActionStart_LogsUnderActionNode()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness();

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("RunScript") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var actionNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Action);
        actionNode.ShouldNotBeNull();

        var actionLog = logs.FirstOrDefault(l => l.Message.Contains("Running action", StringComparison.OrdinalIgnoreCase) || l.Message.Contains("Executing action", StringComparison.OrdinalIgnoreCase));
        actionLog.ShouldNotBeNull("Should log action start");
    }

    [Fact]
    public async Task ActionSuccess_LogsExitCode()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness();

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("RunScript") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var successLog = logs.FirstOrDefault(l => l.Message.Contains("succeeded", StringComparison.OrdinalIgnoreCase) || l.Message.Contains("exit code", StringComparison.OrdinalIgnoreCase));
        successLog.ShouldNotBeNull("Should log action success with exit code");
    }

    // ========== Condition-Met Confirmation ==========

    [Fact]
    public async Task ConditionMet_Failure_LogsConfirmationBeforeExecution()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness();
        ctx.FailureEncountered = true;

        var step = MakeStep("Rollback step", 1, "web", condition: "Failure");
        step.Actions = new List<DeploymentActionDto> { MakeAction("RunScript") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull();

        var confirmLog = logs.FirstOrDefault(l => l.Message.Contains("failure has been detected", StringComparison.OrdinalIgnoreCase));
        confirmLog.ShouldNotBeNull("Should log condition-met confirmation for Failure condition");
        confirmLog.ActivityNodeId.ShouldBe(stepNode.Id);
        confirmLog.Category.ShouldBe(ServerTaskLogCategory.Info);

        var execLog = logs.FirstOrDefault(l => l.Message.Contains("Executing step", StringComparison.OrdinalIgnoreCase));
        execLog.ShouldNotBeNull("Step should still execute after condition-met confirmation");
    }

    // ========== Step Skipped — Condition Not Met ==========

    [Fact]
    public async Task StepSkipped_SuccessConditionNotMet_LogsSkippedNotSuccess()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness();
        ctx.FailureEncountered = true;

        var step = MakeStep("Deploy web", 1, "web", condition: "Success");
        step.Actions = new List<DeploymentActionDto> { MakeAction("RunScript") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull();

        var skippedLog = logs.FirstOrDefault(l => l.Message.Contains("was skipped", StringComparison.OrdinalIgnoreCase));
        skippedLog.ShouldNotBeNull("Should log step was skipped");

        var successLog = logs.FirstOrDefault(l => l.Message.Contains("completed successfully", StringComparison.OrdinalIgnoreCase));
        successLog.ShouldBeNull("Should NOT log step completed successfully when skipped");
    }

    // ========== Step Failure Suppresses Completed Log ==========

    [Fact]
    public async Task StepFailed_DoesNotLogCompleted()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarnessWithFailingStrategy();

        var step = MakeStep("Deploy web", 1, "web");
        step.IsRequired = false;
        step.Actions = new List<DeploymentActionDto> { MakeAction("RunScript") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var completedLog = logs.FirstOrDefault(l => l.Message.Contains("completed successfully", StringComparison.OrdinalIgnoreCase) && l.Message.Contains(step.Name, StringComparison.OrdinalIgnoreCase));
        completedLog.ShouldBeNull("Should NOT log step completed when step failed");
    }

    // ========== No Matching Targets — Plural/Singular ==========

    [Fact]
    public async Task NoMatchingTargets_SingleRole_UsesSingularForm()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness(machineRoles: "web");

        var step = MakeStep("Deploy cache", 1, "cache");
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var noTargetsLog = logs.FirstOrDefault(l => l.Message.Contains("no machines were found", StringComparison.OrdinalIgnoreCase));
        noTargetsLog.ShouldNotBeNull();
        noTargetsLog.Message.ShouldContain("in the role:");
        noTargetsLog.Message.ShouldNotContain("in the roles:");
    }

    // ========== Deployment Start Log ==========

    [Fact]
    public async Task CreateTaskActivityNode_LogsDeploymentStart()
    {
        var (lifecycle, logs, nodes) = CreateLifecycleHarness();
        var ctx = CreateBaseContext();
        ctx.Project = new Squid.Core.Persistence.Entities.Deployments.Project { Name = "MyApp" };
        ctx.Environment = new Squid.Core.Persistence.Entities.Deployments.Environment { Name = "Production" };
        ctx.Release.Version = "2.0.0";
        lifecycle.Initialize(ctx);

        await lifecycle.EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()), CancellationToken.None);

        var startLog = logs.FirstOrDefault(l => l.Message.Contains("Deploying", StringComparison.OrdinalIgnoreCase) && l.Message.Contains("MyApp", StringComparison.Ordinal));
        startLog.ShouldNotBeNull("Should log deployment start message");
        startLog.Message.ShouldContain("2.0.0");
        startLog.Message.ShouldContain("Production");
        startLog.Category.ShouldBe(ServerTaskLogCategory.Info);
    }

    // ========== No Matching Targets ==========

    [Fact]
    public async Task NoMatchingTargets_LogsUnderStepNode()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness(machineRoles: "web");

        var step = MakeStep("Deploy cache", 1, "cache");
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull("Step node should be created even when no matching targets");

        var noTargetsLog = logs.FirstOrDefault(l => l.Message.Contains("no machines were found", StringComparison.OrdinalIgnoreCase));
        noTargetsLog.ShouldNotBeNull("Should log no machines found warning");
        noTargetsLog.Category.ShouldBe(ServerTaskLogCategory.Warning);
        noTargetsLog.ActivityNodeId.ShouldBe(stepNode.Id);
    }

    // ========== Step/Deployment Completion ==========

    [Fact]
    public async Task StepCompleted_LogsUnderStepNode()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness();

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("RunScript") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull();

        var completedLog = logs.FirstOrDefault(l => l.Message.Contains("completed", StringComparison.OrdinalIgnoreCase) && l.Message.Contains(step.Name, StringComparison.OrdinalIgnoreCase));
        completedLog.ShouldNotBeNull("Should log step completed");
        completedLog.ActivityNodeId.ShouldBe(stepNode.Id);
    }

    [Fact]
    public async Task DeploymentSuccess_LogsPersistedAtTaskRoot()
    {
        var (lifecycle, logs, nodes) = CreateLifecycleHarness(withCompletionMocks: true);
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        await lifecycle.EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()), CancellationToken.None);
        await lifecycle.EmitAsync(new DeploymentSucceededEvent(new DeploymentEventContext()), CancellationToken.None);

        var taskNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Task);
        var successLog = logs.FirstOrDefault(l => l.Message.Contains("Deployment completed successfully", StringComparison.OrdinalIgnoreCase));
        successLog.ShouldNotBeNull("Should persist deployment success message to task log");
        successLog.Category.ShouldBe(ServerTaskLogCategory.Info);
        successLog.ActivityNodeId.ShouldBe(taskNode?.Id);
    }

    // ========== Packages Acquire / Release ==========

    [Fact]
    public async Task PackagesAcquiring_WithPackages_LogsAcquirePhase()
    {
        var (lifecycle, logs, nodes) = CreateLifecycleHarness();
        var ctx = CreateBaseContext();
        ctx.SelectedPackages = new List<ReleaseSelectedPackage>
        {
            new() { ActionName = "Deploy", Version = "1.0.0" },
            new() { ActionName = "Migrate", Version = "2.0.0" }
        };
        lifecycle.Initialize(ctx);

        await lifecycle.EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()), CancellationToken.None);
        await lifecycle.EmitAsync(new PackagesAcquiringEvent(new DeploymentEventContext { SelectedPackages = ctx.SelectedPackages }), CancellationToken.None);

        var acquireNode = nodes.FirstOrDefault(n => n.Name == "Acquire packages");
        acquireNode.ShouldNotBeNull("Should create 'Acquire packages' activity node");
        acquireNode.NodeType.ShouldBe(DeploymentActivityLogNodeType.Phase);

        logs.ShouldContain(l => l.Message.Contains("Acquiring packages", StringComparison.OrdinalIgnoreCase));
        logs.ShouldContain(l => l.Message.Contains("Package Deploy version 1.0.0", StringComparison.Ordinal));
        logs.ShouldContain(l => l.Message.Contains("Package Migrate version 2.0.0", StringComparison.Ordinal));
        logs.ShouldContain(l => l.Message.Contains("All packages have been acquired", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PackagesAcquiring_NoPackages_LogsNoPackagesToAcquire()
    {
        var (lifecycle, logs, nodes) = CreateLifecycleHarness();
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        await lifecycle.EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()), CancellationToken.None);
        await lifecycle.EmitAsync(new PackagesAcquiringEvent(new DeploymentEventContext { SelectedPackages = null }), CancellationToken.None);

        var acquireNode = nodes.FirstOrDefault(n => n.Name == "Acquire packages");
        acquireNode.ShouldNotBeNull();
        logs.ShouldContain(l => l.Message.Contains("No packages to acquire", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PackagesReleased_LogsReleasePhase()
    {
        var (lifecycle, logs, nodes) = CreateLifecycleHarness();
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        await lifecycle.EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()), CancellationToken.None);
        await lifecycle.EmitAsync(new PackagesReleasedEvent(new DeploymentEventContext()), CancellationToken.None);

        var releaseNode = nodes.FirstOrDefault(n => n.Name == "Release packages");
        releaseNode.ShouldNotBeNull("Should create 'Release packages' activity node");
        releaseNode.NodeType.ShouldBe(DeploymentActivityLogNodeType.Phase);
        logs.ShouldContain(l => l.Message.Contains("no packages to be released", StringComparison.OrdinalIgnoreCase));
    }

    // ========== StepLevel Action Completion ==========

    [Fact]
    public async Task StepLevelAction_Completed_LogsSuccessNotSkipped()
    {
        var (phase, ctx, logs, nodes) = CreateStepLevelTestHarness();

        var action = MakeAction("Approve");
        action.ActionType = "Squid.ManualIntervention";
        var step = MakeStep("Manual Approval", 1, "web");
        step.Actions = new List<DeploymentActionDto> { action };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull();

        var completedLog = logs.FirstOrDefault(l => l.Message.Contains("completed successfully", StringComparison.OrdinalIgnoreCase));
        completedLog.ShouldNotBeNull("StepLevel step should log completed successfully, not skipped");

        var skippedLog = logs.FirstOrDefault(l => l.Message.Contains("was skipped", StringComparison.OrdinalIgnoreCase));
        skippedLog.ShouldBeNull("StepLevel step should NOT be logged as skipped");
    }

    // ========== Script Output ==========

    [Fact]
    public async Task ScriptOutput_PersistsRawLines()
    {
        var (phase, ctx, logs, nodes) = CreateTestHarness();

        var strategy = new ScriptOutputStrategy(new List<string> { "hello", "world", "done" });
        var transport = new TestTransport(strategy, scriptWrapper: null);
        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("target-1", "web", transport)
        };

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("RunScript") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var scriptLogs = logs.Where(l => l.Message is "hello" or "world" or "done").OrderBy(l => l.Message).ToList();
        scriptLogs.Count.ShouldBe(3);
        scriptLogs[0].Message.ShouldBe("done");
        scriptLogs[1].Message.ShouldBe("hello");
        scriptLogs[2].Message.ShouldBe("world");
    }

    // ========== Test Infrastructure ==========

    private static (ExecuteStepsPhase Phase, DeploymentTaskContext Ctx, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes) CreateTestHarness(string machineRoles = "web")
    {
        var logs = new ConcurrentBag<CapturedLog>();
        var nodes = new ConcurrentBag<ActivityLog>();
        var strategy = new SuccessStrategy();
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);
        var transport = new TestTransport(strategy, scriptWrapper: null);

        var lifecycle = CreateLifecycle(logs, nodes);
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object);

        var ctx = CreateBaseContext();
        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("target-1", machineRoles, transport)
        };

        lifecycle.Initialize(ctx);

        return (phase, ctx, logs, nodes);
    }

    private static (ExecuteStepsPhase Phase, DeploymentTaskContext Ctx, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes) CreateTestHarnessWithFailingStrategy()
    {
        var logs = new ConcurrentBag<CapturedLog>();
        var nodes = new ConcurrentBag<ActivityLog>();
        var strategy = new FailingStrategy();
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);
        var transport = new TestTransport(strategy, scriptWrapper: null);

        var lifecycle = CreateLifecycle(logs, nodes);
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object);

        var ctx = CreateBaseContext();
        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("target-1", "web", transport)
        };

        lifecycle.Initialize(ctx);

        return (phase, ctx, logs, nodes);
    }

    private static (ExecuteStepsPhase Phase, DeploymentTaskContext Ctx, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes) CreateStepLevelTestHarness()
    {
        var logs = new ConcurrentBag<CapturedLog>();
        var nodes = new ConcurrentBag<ActivityLog>();
        var stepLevelHandler = new SimpleStepLevelHandler();

        var registryMock = new Mock<IActionHandlerRegistry>();
        registryMock.Setup(r => r.Resolve(It.IsAny<DeploymentActionDto>())).Returns(stepLevelHandler);
        registryMock.Setup(r => r.ResolveScope(It.IsAny<DeploymentActionDto>())).Returns(ExecutionScope.StepLevel);

        var lifecycle = CreateLifecycle(logs, nodes);
        var phase = new ExecuteStepsPhase(registryMock.Object, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object);

        var ctx = CreateBaseContext();
        ctx.AllTargetsContext = new List<DeploymentTargetContext>();

        lifecycle.Initialize(ctx);

        return (phase, ctx, logs, nodes);
    }

    private static (IDeploymentLifecycle Lifecycle, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes) CreateLifecycleHarness(bool withCompletionMocks = false)
    {
        var logs = new ConcurrentBag<CapturedLog>();
        var nodes = new ConcurrentBag<ActivityLog>();
        var lifecycle = CreateLifecycle(logs, nodes);
        return (lifecycle, logs, nodes);
    }

    private static IDeploymentLifecycle CreateLifecycle(ConcurrentBag<CapturedLog> logs, ConcurrentBag<ActivityLog> nodes)
    {
        var logWriter = CreateLogWriterMock(logs, nodes);
        var logger = new DeploymentActivityLogger(logWriter.Object);
        return new DeploymentLifecyclePublisher(new IDeploymentLifecycleHandler[] { logger });
    }

    private static Mock<IDeploymentLogWriter> CreateLogWriterMock(ConcurrentBag<CapturedLog> logs, ConcurrentBag<ActivityLog> nodes)
    {
        var nextNodeId = 0L;
        var mock = new Mock<IDeploymentLogWriter>();

        mock.Setup(x => x.AddActivityNodeAsync(It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<string>(), It.IsAny<DeploymentActivityLogNodeType>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int taskId, long? parentId, string name, DeploymentActivityLogNodeType nodeType, DeploymentActivityLogNodeStatus status, int sortOrder, CancellationToken _) =>
            {
                var node = new ActivityLog
                {
                    Id = Interlocked.Increment(ref nextNodeId),
                    ServerTaskId = taskId,
                    ParentId = parentId,
                    Name = name,
                    NodeType = nodeType,
                    Status = status,
                    SortOrder = sortOrder,
                    StartedAt = DateTimeOffset.UtcNow
                };
                nodes.Add(node);
                return node;
            });

        mock.Setup(x => x.UpdateActivityNodeStatusAsync(It.IsAny<long>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.AddLogAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<ServerTaskLogCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Callback((int taskId, long seq, ServerTaskLogCategory cat, string msg, string src, long? nodeId, DateTimeOffset? at, CancellationToken _) =>
            {
                logs.Add(new CapturedLog(cat, msg, src, nodeId));
            })
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.AddLogsAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<ServerTaskLogWriteEntry>>(), It.IsAny<CancellationToken>()))
            .Callback((int taskId, IReadOnlyCollection<ServerTaskLogWriteEntry> entries, CancellationToken _) =>
            {
                foreach (var entry in entries)
                    logs.Add(new CapturedLog(entry.Category, entry.MessageText, entry.Source, entry.ActivityNodeId));
            })
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.FlushAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.GetTreeByTaskIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActivityLog>());

        return mock;
    }

    private static DeploymentTaskContext CreateBaseContext()
    {
        return new DeploymentTaskContext
        {
            ServerTaskId = 1001,
            Task = new Squid.Core.Persistence.Entities.Deployments.ServerTask { Id = 1001 },
            Deployment = new Deployment
            {
                Id = 2001,
                EnvironmentId = 1,
                ChannelId = 1
            },
            Release = new Squid.Core.Persistence.Entities.Deployments.Release
            {
                Id = 3001,
                Version = "1.0.0"
            },
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<Squid.Core.Persistence.Entities.Deployments.ReleaseSelectedPackage>()
        };
    }

    private static DeploymentTargetContext MakeTarget(string name, string roles, IDeploymentTransport transport)
    {
        return new DeploymentTargetContext
        {
            Machine = new Machine
            {
                Name = name,
                Roles = System.Text.Json.JsonSerializer.Serialize(roles.Split(',', StringSplitOptions.TrimEntries))
            },
            EndpointContext = new EndpointContext { EndpointJson = "{}" },
            Transport = transport,
            CommunicationStyle = transport.CommunicationStyle
        };
    }

    private static DeploymentStepDto MakeStep(string name, int order, string targetRoles, bool isDisabled = false, string condition = "Success")
    {
        return new DeploymentStepDto
        {
            Id = order,
            Name = name,
            StepOrder = order,
            StartTrigger = string.Empty,
            Condition = condition,
            IsRequired = true,
            IsDisabled = isDisabled,
            Properties = new List<DeploymentStepPropertyDto>
            {
                new()
                {
                    StepId = order,
                    PropertyName = SpecialVariables.Step.TargetRoles,
                    PropertyValue = targetRoles
                }
            },
            Actions = new List<DeploymentActionDto>()
        };
    }

    private static DeploymentActionDto MakeAction(string name)
    {
        return new DeploymentActionDto
        {
            Id = name.GetHashCode(),
            Name = name,
            ActionOrder = 1,
            ActionType = "Squid.Script",
            IsRequired = true,
            IsDisabled = false,
            Properties = new List<DeploymentActionPropertyDto>(),
            Environments = new List<int>(),
            ExcludedEnvironments = new List<int>(),
            Channels = new List<int>()
        };
    }

    private sealed class TestTransport : IDeploymentTransport
    {
        public TestTransport(IExecutionStrategy strategy, IScriptContextWrapper scriptWrapper)
        {
            Strategy = strategy;
            ScriptWrapper = scriptWrapper;
        }

        public CommunicationStyle CommunicationStyle => CommunicationStyle.KubernetesAgent;
        public IEndpointVariableContributor Variables => null;
        public IScriptContextWrapper ScriptWrapper { get; }
        public IExecutionStrategy Strategy { get; }
        public IHealthCheckStrategy HealthChecker => null;
        public ExecutionLocation ExecutionLocation => ExecutionLocation.Unspecified;
        public ExecutionBackend ExecutionBackend => ExecutionBackend.Unspecified;
        public bool RequiresContextPreparationForPackagedPayload => false;
    }

    private sealed class SuccessStrategy : IExecutionStrategy
    {
        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            return Task.FromResult(new ScriptExecutionResult
            {
                Success = true,
                ExitCode = 0,
                LogLines = new List<string>()
            });
        }
    }

    private sealed class ScriptOutputStrategy : IExecutionStrategy
    {
        private readonly List<string> _lines;

        public ScriptOutputStrategy(List<string> lines) => _lines = lines;

        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            return Task.FromResult(new ScriptExecutionResult
            {
                Success = true,
                ExitCode = 0,
                LogLines = _lines
            });
        }
    }

    private sealed class FailingStrategy : IExecutionStrategy
    {
        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            return Task.FromResult(new ScriptExecutionResult
            {
                Success = false,
                ExitCode = 1,
                LogLines = new List<string> { "Error: script failed" }
            });
        }
    }

    private sealed class SimpleRunScriptHandler : IActionHandler
    {
        public string ActionType => "Squid.Script";

        public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
        {
            return Task.FromResult(new ActionExecutionResult
            {
                ScriptBody = $"echo ACTION={ctx.Action.Name}",
                Syntax = ScriptSyntax.Bash,
                ExecutionMode = ExecutionMode.DirectScript,
                ContextPreparationPolicy = ContextPreparationPolicy.Apply
            });
        }
    }

    private sealed class SimpleStepLevelHandler : IActionHandler
    {
        public string ActionType => "Squid.Manual";

        public ExecutionScope ExecutionScope => ExecutionScope.StepLevel;

        public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
            => Task.FromResult(new ActionExecutionResult());

        public Task ExecuteStepLevelAsync(StepActionContext ctx, CancellationToken ct)
            => Task.CompletedTask;
    }
}
