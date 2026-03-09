using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.Certificates;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Environments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments;

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
        var (executor, ctx, logs, nodes) = CreateTestHarness();

        var step = MakeStep("Deploy disabled", 1, "web", isDisabled: true);
        ctx.Steps = new List<DeploymentStepDto> { step };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull("Step node should be created even when skipping");

        var skipLog = logs.FirstOrDefault(l => l.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase));
        skipLog.ShouldNotBeNull("Should log a disabled skip message");
        skipLog.ActivityNodeId.ShouldBe(stepNode.Id);
    }

    [Fact]
    public async Task StepSkip_SuccessCondition_LogsUnderStepNode()
    {
        var (executor, ctx, logs, nodes) = CreateTestHarness();
        ctx.FailureEncountered = true;

        var step = MakeStep("Deploy on success", 1, "web", condition: "Success");
        ctx.Steps = new List<DeploymentStepDto> { step };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull();

        var skipLog = logs.FirstOrDefault(l => l.Message.Contains("previous step failed", StringComparison.OrdinalIgnoreCase));
        skipLog.ShouldNotBeNull("Should log that a previous step failed");
        skipLog.ActivityNodeId.ShouldBe(stepNode.Id);
    }

    [Fact]
    public async Task StepSkip_FailureCondition_LogsUnderStepNode()
    {
        var (executor, ctx, logs, nodes) = CreateTestHarness();
        ctx.FailureEncountered = false;

        var step = MakeStep("Rollback on failure", 1, "web", condition: "Failure");
        ctx.Steps = new List<DeploymentStepDto> { step };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull();

        var skipLog = logs.FirstOrDefault(l => l.Message.Contains("no previous step has failed", StringComparison.OrdinalIgnoreCase));
        skipLog.ShouldNotBeNull("Should log that no previous step has failed");
        skipLog.ActivityNodeId.ShouldBe(stepNode.Id);
    }

    [Fact]
    public async Task StepSkip_RoleMismatch_LogsUnderStepNode()
    {
        var (executor, ctx, logs, nodes) = CreateTestHarness(machineRoles: "web");

        var step = MakeStep("Deploy database", 1, "database");
        ctx.Steps = new List<DeploymentStepDto> { step };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

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
        var (executor, ctx, logs, nodes) = CreateTestHarness();

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("RunScript") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull();

        var execLog = logs.FirstOrDefault(l => l.Message.Contains("Executing step", StringComparison.OrdinalIgnoreCase));
        execLog.ShouldNotBeNull("Should log step execution start");
        execLog.ActivityNodeId.ShouldBe(stepNode.Id);
    }

    // ========== Action Skip Logging ==========

    [Fact]
    public async Task ActionSkip_Disabled_LogsPersistedWarning()
    {
        var (executor, ctx, logs, nodes) = CreateTestHarness();

        var action = MakeAction("RunScript");
        action.IsDisabled = true;
        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { action };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var skipLog = logs.FirstOrDefault(l => l.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase) && l.Message.Contains("RunScript", StringComparison.OrdinalIgnoreCase));
        skipLog.ShouldNotBeNull("Should persist action disabled skip to task log");
        skipLog.Category.ShouldBe(ServerTaskLogCategory.Warning);
    }

    [Fact]
    public async Task ActionSkip_EnvironmentMismatch_LogsPersisted()
    {
        var (executor, ctx, logs, nodes) = CreateTestHarness();

        var action = MakeAction("RunScript");
        action.Environments = new List<int> { 99 };
        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { action };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var skipLog = logs.FirstOrDefault(l => l.Message.Contains("environment", StringComparison.OrdinalIgnoreCase) && l.Message.Contains("RunScript", StringComparison.OrdinalIgnoreCase));
        skipLog.ShouldNotBeNull("Should persist environment mismatch skip to task log");
        skipLog.Category.ShouldBe(ServerTaskLogCategory.Warning);
    }

    [Fact]
    public async Task ActionSkip_ChannelMismatch_LogsPersisted()
    {
        var (executor, ctx, logs, nodes) = CreateTestHarness();

        var action = MakeAction("RunScript");
        action.Channels = new List<int> { 99 };
        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { action };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var skipLog = logs.FirstOrDefault(l => l.Message.Contains("channel", StringComparison.OrdinalIgnoreCase) && l.Message.Contains("RunScript", StringComparison.OrdinalIgnoreCase));
        skipLog.ShouldNotBeNull("Should persist channel mismatch skip to task log");
        skipLog.Category.ShouldBe(ServerTaskLogCategory.Warning);
    }

    [Fact]
    public async Task ActionSkip_ManuallySkipped_LogsPersisted()
    {
        var (executor, ctx, logs, nodes) = CreateTestHarness();

        var action = MakeAction("RunScript");
        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { action };
        ctx.Steps = new List<DeploymentStepDto> { step };
        ctx.Deployment.DeploymentRequestPayload = new DeploymentRequestPayload
        {
            SkipActionIds = new List<int> { action.Id }
        };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var skipLog = logs.FirstOrDefault(l => l.Message.Contains("manually excluded", StringComparison.OrdinalIgnoreCase) || l.Message.Contains("skip", StringComparison.OrdinalIgnoreCase));
        skipLog.ShouldNotBeNull("Should persist manually skipped action to task log");
    }

    // ========== Action Execution Logging ==========

    [Fact]
    public async Task ActionStart_LogsUnderActionNode()
    {
        var (executor, ctx, logs, nodes) = CreateTestHarness();

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("RunScript") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var actionNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Action);
        actionNode.ShouldNotBeNull();

        var actionLog = logs.FirstOrDefault(l => l.Message.Contains("Running action", StringComparison.OrdinalIgnoreCase) || l.Message.Contains("Executing action", StringComparison.OrdinalIgnoreCase));
        actionLog.ShouldNotBeNull("Should log action start");
    }

    [Fact]
    public async Task ActionSuccess_LogsExitCode()
    {
        var (executor, ctx, logs, nodes) = CreateTestHarness();

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("RunScript") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var successLog = logs.FirstOrDefault(l => l.Message.Contains("succeeded", StringComparison.OrdinalIgnoreCase) || l.Message.Contains("exit code", StringComparison.OrdinalIgnoreCase));
        successLog.ShouldNotBeNull("Should log action success with exit code");
    }

    // ========== Condition-Met Confirmation ==========

    [Fact]
    public async Task ConditionMet_Failure_LogsConfirmationBeforeExecution()
    {
        var (executor, ctx, logs, nodes) = CreateTestHarness();
        ctx.FailureEncountered = true;

        var step = MakeStep("Rollback step", 1, "web", condition: "Failure");
        step.Actions = new List<DeploymentActionDto> { MakeAction("RunScript") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull();

        var confirmLog = logs.FirstOrDefault(l => l.Message.Contains("failure has been detected", StringComparison.OrdinalIgnoreCase));
        confirmLog.ShouldNotBeNull("Should log condition-met confirmation for Failure condition");
        confirmLog.ActivityNodeId.ShouldBe(stepNode.Id);
        confirmLog.Category.ShouldBe(ServerTaskLogCategory.Info);

        var execLog = logs.FirstOrDefault(l => l.Message.Contains("Executing step", StringComparison.OrdinalIgnoreCase));
        execLog.ShouldNotBeNull("Step should still execute after condition-met confirmation");
    }

    // ========== Step Failure Suppresses Completed Log ==========

    [Fact]
    public async Task StepFailed_DoesNotLogCompleted()
    {
        var (executor, ctx, logs, nodes) = CreateTestHarnessWithFailingStrategy();

        var step = MakeStep("Deploy web", 1, "web");
        step.IsRequired = false;
        step.Actions = new List<DeploymentActionDto> { MakeAction("RunScript") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var completedLog = logs.FirstOrDefault(l => l.Message.Contains("completed successfully", StringComparison.OrdinalIgnoreCase) && l.Message.Contains(step.Name, StringComparison.OrdinalIgnoreCase));
        completedLog.ShouldBeNull("Should NOT log step completed when step failed");
    }

    // ========== No Matching Targets — Plural/Singular ==========

    [Fact]
    public async Task NoMatchingTargets_SingleRole_UsesSingularForm()
    {
        var (executor, ctx, logs, nodes) = CreateTestHarness(machineRoles: "web");

        var step = MakeStep("Deploy cache", 1, "cache");
        ctx.Steps = new List<DeploymentStepDto> { step };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var noTargetsLog = logs.FirstOrDefault(l => l.Message.Contains("no machines were found", StringComparison.OrdinalIgnoreCase));
        noTargetsLog.ShouldNotBeNull();
        noTargetsLog.Message.ShouldContain("in the role:");
        noTargetsLog.Message.ShouldNotContain("in the roles:");
    }

    // ========== Deployment Start Log ==========

    [Fact]
    public async Task CreateTaskActivityNode_LogsDeploymentStart()
    {
        var (executor, ctx, logs, nodes) = CreateTestHarnessForFullPipeline();
        ctx.Project = new Squid.Core.Persistence.Entities.Deployments.Project { Name = "MyApp" };
        ctx.Environment = new Squid.Core.Persistence.Entities.Deployments.Environment { Name = "Production" };
        ctx.Release.Version = "2.0.0";

        await InvokeCreateTaskActivityNodeAsync(executor, ctx);

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
        var (executor, ctx, logs, nodes) = CreateTestHarness(machineRoles: "web");

        var step = MakeStep("Deploy cache", 1, "cache");
        ctx.Steps = new List<DeploymentStepDto> { step };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

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
        var (executor, ctx, logs, nodes) = CreateTestHarness();

        var step = MakeStep("Deploy web", 1, "web");
        step.Actions = new List<DeploymentActionDto> { MakeAction("RunScript") };
        ctx.Steps = new List<DeploymentStepDto> { step };

        await InvokeExecuteDeploymentStepsAsync(executor, ctx);

        var stepNode = nodes.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Step);
        stepNode.ShouldNotBeNull();

        var completedLog = logs.FirstOrDefault(l => l.Message.Contains("completed", StringComparison.OrdinalIgnoreCase) && l.Message.Contains(step.Name, StringComparison.OrdinalIgnoreCase));
        completedLog.ShouldNotBeNull("Should log step completed");
        completedLog.ActivityNodeId.ShouldBe(stepNode.Id);
    }

    [Fact]
    public async Task DeploymentSuccess_LogsPersistedAtTaskRoot()
    {
        var (executor, ctx, logs, nodes) = CreateTestHarnessForFullPipeline();

        await InvokeRecordSuccessAsync(executor, ctx);

        var successLog = logs.FirstOrDefault(l => l.Message.Contains("Deployment completed successfully", StringComparison.OrdinalIgnoreCase));
        successLog.ShouldNotBeNull("Should persist deployment success message to task log");
        successLog.Category.ShouldBe(ServerTaskLogCategory.Info);
        successLog.ActivityNodeId.ShouldBe(ctx.TaskActivityNode?.Id);
    }

    // ========== Test Infrastructure ==========

    private static (DeploymentTaskExecutor Executor, DeploymentTaskContext Ctx, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes) CreateTestHarness(string machineRoles = "web")
    {
        var logs = new ConcurrentBag<CapturedLog>();
        var nodes = new ConcurrentBag<ActivityLog>();
        var strategy = new SuccessStrategy();
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);
        var transport = new TestTransport(strategy, scriptWrapper: null);

        var executor = CreateExecutor(registry, logs, nodes);

        var ctx = CreateBaseContext();
        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("target-1", machineRoles, transport)
        };

        SetContext(executor, ctx);

        return (executor, ctx, logs, nodes);
    }

    private static (DeploymentTaskExecutor Executor, DeploymentTaskContext Ctx, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes) CreateTestHarnessWithFailingStrategy()
    {
        var logs = new ConcurrentBag<CapturedLog>();
        var nodes = new ConcurrentBag<ActivityLog>();
        var strategy = new FailingStrategy();
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);
        var transport = new TestTransport(strategy, scriptWrapper: null);

        var executor = CreateExecutor(registry, logs, nodes);

        var ctx = CreateBaseContext();
        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("target-1", "web", transport)
        };

        SetContext(executor, ctx);

        return (executor, ctx, logs, nodes);
    }

    private static (DeploymentTaskExecutor Executor, DeploymentTaskContext Ctx, ConcurrentBag<CapturedLog> Logs, ConcurrentBag<ActivityLog> Nodes) CreateTestHarnessForFullPipeline()
    {
        var logs = new ConcurrentBag<CapturedLog>();
        var nodes = new ConcurrentBag<ActivityLog>();
        var registry = Mock.Of<IActionHandlerRegistry>();

        var deploymentCompletionMock = new Mock<IDeploymentCompletionDataProvider>();
        deploymentCompletionMock
            .Setup(x => x.AddDeploymentCompletionAsync(It.IsAny<DeploymentCompletion>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var deploymentDataMock = new Mock<IDeploymentDataProvider>();
        deploymentDataMock
            .Setup(x => x.GetDeploymentByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Deployment { Id = 2001, SpaceId = 1 });

        var executor = CreateExecutor(registry, logs, nodes, deploymentCompletionMock.Object, deploymentDataMock.Object);

        var ctx = CreateBaseContext();
        ctx.TaskActivityNode = new ActivityLog { Id = 100, Name = "Task Root" };

        SetContext(executor, ctx);

        return (executor, ctx, logs, nodes);
    }

    private static void SetContext(DeploymentTaskExecutor executor, DeploymentTaskContext ctx)
    {
        var ctxField = typeof(DeploymentTaskExecutor).GetField("_ctx", BindingFlags.Instance | BindingFlags.NonPublic);
        ctxField.ShouldNotBeNull();
        ctxField.SetValue(executor, ctx);
    }

    private static async Task InvokeExecuteDeploymentStepsAsync(DeploymentTaskExecutor executor, DeploymentTaskContext ctx)
    {
        SetContext(executor, ctx);

        var method = typeof(DeploymentTaskExecutor).GetMethod("ExecuteDeploymentStepsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        method.ShouldNotBeNull();

        var task = (Task)method.Invoke(executor, new object[] { CancellationToken.None })!;
        await task.ConfigureAwait(false);
    }

    private static async Task InvokeCreateTaskActivityNodeAsync(DeploymentTaskExecutor executor, DeploymentTaskContext ctx)
    {
        SetContext(executor, ctx);

        var method = typeof(DeploymentTaskExecutor).GetMethod("CreateTaskActivityNodeAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        method.ShouldNotBeNull();

        var task = (Task)method.Invoke(executor, new object[] { CancellationToken.None })!;
        await task.ConfigureAwait(false);
    }

    private static async Task InvokeRecordSuccessAsync(DeploymentTaskExecutor executor, DeploymentTaskContext ctx)
    {
        SetContext(executor, ctx);

        var method = typeof(DeploymentTaskExecutor).GetMethod("RecordSuccessAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        method.ShouldNotBeNull();

        var task = (Task)method.Invoke(executor, new object[] { CancellationToken.None })!;
        await task.ConfigureAwait(false);
    }

    private static DeploymentTaskExecutor CreateExecutor(
        IActionHandlerRegistry registry,
        ConcurrentBag<CapturedLog> logs,
        ConcurrentBag<ActivityLog> nodes,
        IDeploymentCompletionDataProvider completionProvider = null,
        IDeploymentDataProvider deploymentDataProvider = null)
    {
        var nextNodeId = 0L;
        var serverTaskServiceMock = new Mock<IServerTaskService>();

        serverTaskServiceMock
            .Setup(x => x.AddActivityNodeAsync(
                It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<string>(),
                It.IsAny<DeploymentActivityLogNodeType>(), It.IsAny<DeploymentActivityLogNodeStatus>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
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

        serverTaskServiceMock
            .Setup(x => x.UpdateActivityNodeStatusAsync(It.IsAny<long>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        serverTaskServiceMock
            .Setup(x => x.AddLogAsync(
                It.IsAny<int>(), It.IsAny<long>(), It.IsAny<ServerTaskLogCategory>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long?>(),
                It.IsAny<DateTimeOffset?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((int taskId, long seq, ServerTaskLogCategory cat, string msg, string src, long? nodeId, DateTimeOffset? at, string detail, CancellationToken _) =>
            {
                logs.Add(new CapturedLog(cat, msg, src, nodeId));
            })
            .Returns(Task.CompletedTask);

        serverTaskServiceMock
            .Setup(x => x.AddLogsAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<ServerTaskLogWriteEntry>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        serverTaskServiceMock
            .Setup(x => x.TransitionStateAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var genericDataMock = new Mock<IGenericDataProvider>();
        genericDataMock
            .Setup(x => x.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task> action, CancellationToken ct) => action(ct));

        return new DeploymentTaskExecutor(
            genericDataMock.Object,
            Mock.Of<IReleaseDataProvider>(),
            Mock.Of<IReleaseSelectedPackageDataProvider>(),
            serverTaskServiceMock.Object,
            deploymentDataProvider ?? Mock.Of<IDeploymentDataProvider>(),
            Mock.Of<IProjectDataProvider>(),
            Mock.Of<IEnvironmentDataProvider>(),
            Mock.Of<IDeploymentAccountDataProvider>(),
            Mock.Of<ICertificateDataProvider>(),
            completionProvider ?? Mock.Of<IDeploymentCompletionDataProvider>(),
            Mock.Of<IYamlNuGetPacker>(),
            Mock.Of<IDeploymentTargetFinder>(),
            Mock.Of<IDeploymentSnapshotService>(),
            Mock.Of<IDeploymentVariableResolver>(),
            registry,
            Mock.Of<ITransportRegistry>(),
            Mock.Of<IAutoDeployService>());
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
                    PropertyName = DeploymentVariables.Action.TargetRoles,
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
            ActionType = "Squid.KubernetesRunScript",
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
        public DeploymentActionType ActionType => DeploymentActionType.KubernetesRunScript;

        public bool CanHandle(DeploymentActionDto action)
            => DeploymentActionTypeParser.Is(action?.ActionType, ActionType);

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
}
