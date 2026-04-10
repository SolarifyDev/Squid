using System.Collections.Concurrent;
using System.Collections.Generic;
using System;
using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Message.Constants;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class DeploymentTaskExecutorPhase4AcceptanceTests
{
    [Fact]
    public async Task CreateTaskActivityNode_UsesSquidStyleTitle()
    {
        var createdNodes = new List<(DeploymentActivityLogNodeType NodeType, string Name)>();
        var (lifecycle, _) = CreateLifecycle((nodeType, name) => createdNodes.Add((nodeType, name)));
        var ctx = CreateBaseContext();

        ctx.Project = new Project { Name = "Smarties.Api" };
        ctx.Environment = new Squid.Core.Persistence.Entities.Deployments.Environment { Name = "TEST" };
        ctx.Release.Version = "6.2.5-mixture-v6.1-4016";

        lifecycle.Initialize(ctx);
        await lifecycle.EmitAsync(new DeploymentStartingEvent(new DeploymentEventContext()), CancellationToken.None);

        createdNodes.ShouldContain(x =>
            x.NodeType == DeploymentActivityLogNodeType.Task &&
            x.Name == "Deploy Smarties.Api release 6.2.5-mixture-v6.1-4016 to TEST");
    }

    [Fact]
    public async Task ExecuteDeploymentSteps_SameBatch_DoesNotSeeOutputVariables_UntilNextBatch()
    {
        var barrier = new AsyncBarrier(2);
        var strategy = new RecordingStrategy();
        var handler = new CoordinatedRunScriptHandler(barrier);
        var registry = Mock.Of<IActionHandlerRegistry>(r =>
            r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var transport = new TestTransport(strategy, scriptWrapper: null);
        var (lifecycle, _) = CreateLifecycle();
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        var target = MakeTarget("target-1", "web", transport, endpointJson: "endpoint-a");
        ctx.AllTargetsContext = new List<DeploymentTargetContext> { target };
        ctx.Steps = new List<DeploymentStepDto>
        {
            MakeStep("Step1", 1, null, "web", MakeAction("Action1")),
            MakeStep("Step2", 2, "StartWithPrevious", "web", MakeAction("Action2")),
            MakeStep("Step3", 3, null, "web", MakeAction("Action3"))
        };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var scripts = strategy.Requests.Select(r => r.ScriptBody).ToList();
        scripts.ShouldContain(s => s.Contains("ACTION=Action2", StringComparison.Ordinal) &&
                                   s.Contains("SEES_X=False", StringComparison.Ordinal));
        scripts.ShouldContain(s => s.Contains("ACTION=Action3", StringComparison.Ordinal) &&
                                   s.Contains("SEES_X=True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteDeploymentSteps_SameBatch_ParallelSteps_DoNotUseWrongTargetWrapperContext()
    {
        var barrier = new AsyncBarrier(2);
        var strategy = new RecordingStrategy();
        var wrapper = new EndpointStampingWrapper();
        var handler = new CoordinatedRunScriptHandler(barrier);
        var registry = Mock.Of<IActionHandlerRegistry>(r =>
            r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var transport = new TestTransport(strategy, wrapper);
        var (lifecycle, _) = CreateLifecycle();
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("machine-a", "role-a", transport, endpointJson: "endpoint-a"),
            MakeTarget("machine-b", "role-b", transport, endpointJson: "endpoint-b")
        };

        ctx.Steps = new List<DeploymentStepDto>
        {
            MakeStep("StepA", 1, null, "role-a", MakeAction("ActionA")),
            MakeStep("StepB", 2, "StartWithPrevious", "role-b", MakeAction("ActionB"))
        };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var requestsByMachine = strategy.Requests.ToDictionary(r => r.Machine.Name, r => r.ScriptBody);

        requestsByMachine["machine-a"].ShouldContain("WRAPPED_ENDPOINT=endpoint-a");
        requestsByMachine["machine-b"].ShouldContain("WRAPPED_ENDPOINT=endpoint-b");
        requestsByMachine["machine-a"].ShouldNotContain("endpoint-b");
        requestsByMachine["machine-b"].ShouldNotContain("endpoint-a");
    }

    [Fact]
    public async Task ExecuteDeploymentSteps_SameBatch_Failure_DoesNotAffectSiblingStep_ButAffectsNextBatch()
    {
        var barrier = new AsyncBarrier(2);
        var strategy = new RecordingStrategy();
        strategy.FailActions.Add("Action1");

        var handler = new CoordinatedRunScriptHandler(barrier);
        var registry = Mock.Of<IActionHandlerRegistry>(r =>
            r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var transport = new TestTransport(strategy, scriptWrapper: null);
        var (lifecycle, _) = CreateLifecycle();
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("target-1", "web", transport, endpointJson: "endpoint-a")
        };

        var step1 = MakeStep("Step1", 1, null, "web", MakeAction("Action1"));
        step1.IsRequired = false; // allow batch to complete and merge failure after join

        var step2 = MakeStep("Step2", 2, "StartWithPrevious", "web", MakeAction("Action2"));
        step2.Condition = "Success";

        var step3 = MakeStep("Step3", 3, null, "web", MakeAction("Action3"));
        step3.Condition = "Success";

        ctx.Steps = new List<DeploymentStepDto> { step1, step2, step3 };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        var executedActions = strategy.Requests
            .Select(x => x.ScriptBody)
            .Where(x => x != null)
            .ToList();

        executedActions.ShouldContain(x => x.Contains("ACTION=Action1", StringComparison.Ordinal));
        executedActions.ShouldContain(x => x.Contains("ACTION=Action2", StringComparison.Ordinal));
        executedActions.ShouldNotContain(x => x.Contains("ACTION=Action3", StringComparison.Ordinal));
        ctx.FailureEncountered.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteDeploymentSteps_UsesStepOrderAndWorkerNamingForActivityNodes()
    {
        var strategy = new RecordingStrategy();
        var handler = new CoordinatedRunScriptHandler(new AsyncBarrier(1));
        var createdNodes = new List<(DeploymentActivityLogNodeType NodeType, string Name)>();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);
        var transport = new TestTransport(strategy, scriptWrapper: null);
        var (lifecycle, _) = CreateLifecycle((nodeType, name) => createdNodes.Add((nodeType, name)));
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        ctx.AllTargetsContext = new List<DeploymentTargetContext> { MakeTarget("SJ-US-AKS", "web", transport, endpointJson: "endpoint-a") };
        ctx.Steps = new List<DeploymentStepDto> { MakeStep("Deploy web", 1, null, "web", MakeAction("ActionA")) };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        createdNodes.ShouldContain(x => x.NodeType == DeploymentActivityLogNodeType.Step && x.Name == "Step 1: Deploy web");
        createdNodes.ShouldContain(x => x.NodeType == DeploymentActivityLogNodeType.Action && x.Name == "Executing on SJ-US-AKS");
    }

    [Fact]
    public async Task ExecuteDeploymentSteps_SyntheticAcquirePackagesStep_EmitsPackageEventWithoutScriptExecution()
    {
        var strategy = new RecordingStrategy();
        var handler = new CoordinatedRunScriptHandler(new AsyncBarrier(1));
        var createdNodes = new List<(DeploymentActivityLogNodeType NodeType, string Name)>();
        var logMessages = new List<string>();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);
        var transport = new TestTransport(strategy, scriptWrapper: null);
        var (lifecycle, logWriter) = CreateLifecycle((nodeType, name) => createdNodes.Add((nodeType, name)));

        logWriter
            .Setup(x => x.AddLogAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<ServerTaskLogCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Callback<int, long, ServerTaskLogCategory, string, string, long?, DateTimeOffset?, CancellationToken>((_, _, _, msg, _, _, _, _) => logMessages.Add(msg))
            .Returns(Task.CompletedTask);

        var externalFeedDataProvider = new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>();
        externalFeedDataProvider.Setup(x => x.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ExternalFeed> { new() { Id = 42, FeedType = "Generic", FeedUri = "https://packages.example.com" } });

        var packageAcquisitionService = new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>();
        packageAcquisitionService.Setup(x => x.AcquireAsync(It.IsAny<ExternalFeed>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Squid.Core.Services.DeploymentExecution.Packages.PackageAcquisitionResult("/tmp/package.nupkg", "Deploy Web", "1.2.3", 123, "ABCDEF0123456789ABCDEF0123456789"));

        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, externalFeedDataProvider.Object, packageAcquisitionService.Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());
        var ctx = CreateBaseContext();
        ctx.SelectedPackages = new List<ReleaseSelectedPackage> { new() { ActionName = "Deploy Web", PackageReferenceName = "Deploy.Web", Version = "1.2.3", FeedId = 42 } };
        lifecycle.Initialize(ctx);

        ctx.AllTargetsContext = new List<DeploymentTargetContext> { MakeTarget("SJ-US-AKS", "web", transport, endpointJson: "endpoint-a") };

        var syntheticStep = MakeStep("Acquire Packages", 1, null, "web", MakeAction("AcquireAction"));
        syntheticStep.StepType = "AcquirePackages";

        ctx.Steps = new List<DeploymentStepDto> { syntheticStep };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        strategy.Requests.ShouldBeEmpty();
        createdNodes.ShouldNotContain(x => x.NodeType == DeploymentActivityLogNodeType.Step && x.Name.Contains("Acquire"));
        createdNodes.ShouldContain(x => x.NodeType == DeploymentActivityLogNodeType.Phase && x.Name == "Acquire packages");
        logMessages.ShouldContain("Acquiring packages");
        logMessages.ShouldContain("Package Deploy Web version 1.2.3");
        logMessages.ShouldContain("All packages have been acquired");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("1", false)]
    public async Task ExecuteDeploymentSteps_MultiTarget_RespectsMaxParallelism(string maxParallelism, bool expectConcurrent)
    {
        var strategy = new ConcurrencyTrackingStrategy();
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);
        var transport = new TestTransport(strategy, scriptWrapper: null);
        var (lifecycle, _) = CreateLifecycle();
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("target-1", "web", transport, endpointJson: "endpoint-a"),
            MakeTarget("target-2", "web", transport, endpointJson: "endpoint-b")
        };

        var step = MakeStep("Step1", 1, null, "web", MakeAction("TargetAction"));

        if (maxParallelism != null)
        {
            step.Properties.Add(new DeploymentStepPropertyDto
            {
                StepId = 1,
                PropertyName = SpecialVariables.Step.MaxParallelism,
                PropertyValue = maxParallelism
            });
        }

        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        strategy.Requests.Count.ShouldBe(2);

        if (expectConcurrent)
            strategy.MaxObservedConcurrency.ShouldBeGreaterThan(1);
        else
            strategy.MaxObservedConcurrency.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteDeploymentSteps_MultiTarget_ThrottledParallelism_RespectsLimit()
    {
        var strategy = new ConcurrencyTrackingStrategy();
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);
        var transport = new TestTransport(strategy, scriptWrapper: null);
        var (lifecycle, _) = CreateLifecycle();
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("target-1", "web", transport, endpointJson: "endpoint-a"),
            MakeTarget("target-2", "web", transport, endpointJson: "endpoint-b"),
            MakeTarget("target-3", "web", transport, endpointJson: "endpoint-c")
        };

        var step = MakeStep("Step1", 1, null, "web", MakeAction("TargetAction"));
        step.Properties.Add(new DeploymentStepPropertyDto { StepId = 1, PropertyName = SpecialVariables.Step.MaxParallelism, PropertyValue = "2" });
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        strategy.Requests.Count.ShouldBe(3);
        strategy.MaxObservedConcurrency.ShouldBeGreaterThan(1);
        strategy.MaxObservedConcurrency.ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task ExecuteDeploymentSteps_MultiTarget_RequiredStep_FailFast_CancelsRemainingTargets()
    {
        var strategy = new RecordingStrategy();
        strategy.FailActions.Add("FailAction");
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);
        var transport = new TestTransport(strategy, scriptWrapper: null);
        var (lifecycle, _) = CreateLifecycle();
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("target-1", "web", transport, endpointJson: "endpoint-a"),
            MakeTarget("target-2", "web", transport, endpointJson: "endpoint-b")
        };

        var step = MakeStep("Step1", 1, null, "web", MakeAction("FailAction"));
        step.IsRequired = true;
        step.Properties.Add(new DeploymentStepPropertyDto { StepId = 1, PropertyName = SpecialVariables.Step.MaxParallelism, PropertyValue = "1" });
        ctx.Steps = new List<DeploymentStepDto> { step };

        await Should.ThrowAsync<Exception>(async () => await phase.ExecuteAsync(ctx, CancellationToken.None));

        strategy.Requests.Count.ShouldBe(1, "Fail-fast should stop after first target failure");
    }

    [Fact]
    public async Task ExecuteDeploymentSteps_MultiTarget_NonRequiredStep_ContinuesAfterFailure()
    {
        var strategy = new PerTargetResultStrategy(target => target == "target-1");
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);
        var transport = new TestTransport(strategy, scriptWrapper: null);
        var (lifecycle, _) = CreateLifecycle();
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("target-1", "web", transport, endpointJson: "endpoint-a"),
            MakeTarget("target-2", "web", transport, endpointJson: "endpoint-b")
        };

        var step = MakeStep("Step1", 1, null, "web", MakeAction("SomeAction"));
        step.IsRequired = false;
        step.Properties.Add(new DeploymentStepPropertyDto { StepId = 1, PropertyName = SpecialVariables.Step.MaxParallelism, PropertyValue = "1" });
        ctx.Steps = new List<DeploymentStepDto> { step };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        strategy.Requests.Count.ShouldBe(2, "Both targets should execute even though target-1 failed");
        ctx.FailureEncountered.ShouldBeTrue();
    }

    private static (IDeploymentLifecycle Lifecycle, Mock<IDeploymentLogWriter> LogWriterMock) CreateLifecycle(Action<DeploymentActivityLogNodeType, string> onAddActivityNode = null)
    {
        var nextNodeId = 0L;
        var logWriter = new Mock<IDeploymentLogWriter>();

        logWriter
            .Setup(x => x.AddActivityNodeAsync(It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<string>(), It.IsAny<DeploymentActivityLogNodeType>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int taskId, long? parentId, string name, DeploymentActivityLogNodeType nodeType, DeploymentActivityLogNodeStatus status, int sortOrder, CancellationToken _) =>
            {
                onAddActivityNode?.Invoke(nodeType, name);

                return new Squid.Core.Persistence.Entities.Deployments.ActivityLog
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
            });

        logWriter
            .Setup(x => x.UpdateActivityNodeStatusAsync(It.IsAny<long>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        logWriter
            .Setup(x => x.AddLogAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<ServerTaskLogCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        logWriter
            .Setup(x => x.AddLogsAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<ServerTaskLogWriteEntry>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        logWriter
            .Setup(x => x.FlushAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        logWriter
            .Setup(x => x.GetTreeByTaskIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActivityLog>());

        var logger = new DeploymentActivityLogger(logWriter.Object);
        var lifecycle = new DeploymentLifecyclePublisher(new IDeploymentLifecycleHandler[] { logger });

        return (lifecycle, logWriter);
    }

    private static DeploymentTaskContext CreateBaseContext()
    {
        return new DeploymentTaskContext
        {
            Task = new ServerTaskEntity { Id = 1001 },
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

    private static DeploymentTargetContext MakeTarget(
        string name,
        string roles,
        IDeploymentTransport transport,
        string endpointJson)
    {
        return new DeploymentTargetContext
        {
            Machine = new Machine
            {
                Name = name,
                Roles = System.Text.Json.JsonSerializer.Serialize(new[] { roles })
            },
            EndpointContext = new EndpointContext { EndpointJson = endpointJson },
            Transport = transport,
            CommunicationStyle = transport.CommunicationStyle
        };
    }

    private static DeploymentStepDto MakeStep(
        string name,
        int order,
        string startTrigger,
        string targetRoles,
        DeploymentActionDto action)
    {
        return new DeploymentStepDto
        {
            Id = order,
            Name = name,
            StepOrder = order,
            StartTrigger = startTrigger ?? string.Empty,
            Condition = "Success",
            IsRequired = true,
            IsDisabled = false,
            Properties = new List<DeploymentStepPropertyDto>
            {
                new()
                {
                    StepId = order,
                    PropertyName = SpecialVariables.Step.TargetRoles,
                    PropertyValue = targetRoles
                }
            },
            Actions = new List<DeploymentActionDto> { action }
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
            Properties = new List<DeploymentActionPropertyDto>()
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

    private sealed class EndpointStampingWrapper : IScriptContextWrapper
    {
        public string WrapScript(string script, ScriptContext context)
            => $"WRAPPED_ENDPOINT={context.Endpoint.EndpointJson};{script}";
    }

    private sealed class RecordingStrategy : IExecutionStrategy
    {
        public ConcurrentBag<ScriptExecutionRequest> Requests { get; } = new();
        public HashSet<string> FailActions { get; } = new(StringComparer.Ordinal);

        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            Requests.Add(request);

            var logs = new List<string>();
            if (request.ScriptBody.Contains("ACTION=Action1", StringComparison.Ordinal))
                logs.Add("##squid[setVariable name='X' value='1']");

            var shouldFail = FailActions.Any(actionName =>
                request.ScriptBody.Contains($"ACTION={actionName}", StringComparison.Ordinal));

            return Task.FromResult(new ScriptExecutionResult
            {
                Success = !shouldFail,
                ExitCode = shouldFail ? 1 : 0,
                LogLines = logs
            });
        }
    }

    private sealed class CoordinatedRunScriptHandler : IActionHandler
    {
        private readonly AsyncBarrier _barrier;

        public CoordinatedRunScriptHandler(AsyncBarrier barrier)
        {
            _barrier = barrier;
        }

        public string ActionType => "Squid.Script";

        public async Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
        {
            if (ctx.Action.Name is "Action1" or "Action2" or "ActionA" or "ActionB")
                await _barrier.SignalAndWaitAsync(ct).ConfigureAwait(false);

            var seesX = ctx.Variables?.Any(v => v.Name == "X") == true;

            return new ActionExecutionResult
            {
                ScriptBody = $"ACTION={ctx.Action.Name};SEES_X={seesX}",
                Syntax = ScriptSyntax.Bash,
                ExecutionMode = ExecutionMode.DirectScript,
                ContextPreparationPolicy = ContextPreparationPolicy.Apply
            };
        }
    }

    private sealed class SimpleRunScriptHandler : IActionHandler
    {
        public string ActionType => "Squid.Script";

        public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
        {
            return Task.FromResult(new ActionExecutionResult
            {
                ScriptBody = $"ACTION={ctx.Action.Name}",
                Syntax = ScriptSyntax.Bash,
                ExecutionMode = ExecutionMode.DirectScript,
                ContextPreparationPolicy = ContextPreparationPolicy.Apply
            });
        }
    }

    private sealed class ConcurrencyTrackingStrategy : IExecutionStrategy
    {
        private int _concurrency;

        public ConcurrentBag<ScriptExecutionRequest> Requests { get; } = new();
        public int MaxObservedConcurrency { get; private set; }

        public async Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            Requests.Add(request);

            var current = Interlocked.Increment(ref _concurrency);

            lock (this)
                MaxObservedConcurrency = Math.Max(MaxObservedConcurrency, current);

            await Task.Delay(50, ct).ConfigureAwait(false);
            Interlocked.Decrement(ref _concurrency);

            return new ScriptExecutionResult { Success = true, ExitCode = 0 };
        }
    }

    private sealed class PerTargetResultStrategy : IExecutionStrategy
    {
        private readonly Func<string, bool> _shouldFail;

        public PerTargetResultStrategy(Func<string, bool> shouldFail)
        {
            _shouldFail = shouldFail;
        }

        public ConcurrentBag<ScriptExecutionRequest> Requests { get; } = new();

        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            Requests.Add(request);

            var fail = _shouldFail(request.Machine.Name);

            return Task.FromResult(new ScriptExecutionResult
            {
                Success = !fail,
                ExitCode = fail ? 1 : 0
            });
        }
    }

    private sealed class AsyncBarrier
    {
        private readonly int _participants;
        private int _count;
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AsyncBarrier(int participants)
        {
            _participants = participants;
        }

        public async Task SignalAndWaitAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _count) >= _participants)
                _tcs.TrySetResult();

            using var _ = ct.Register(() => _tcs.TrySetCanceled(ct));
            await _tcs.Task.ConfigureAwait(false);
        }
    }
}
