using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
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
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Message.Constants;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Models.Deployments.Account;
using Squid.Core.Services.Deployments.Account;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class CredentialTypePipelineAcceptanceTests
{
    [Fact]
    public async Task Execute_TwoTargets_DifferentAccountTypes_EachGetsCorrectCredentials()
    {
        var strategy = new RecordingStrategy();
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var transport = new TestTransport(strategy, CommunicationStyle.KubernetesApi);

        var (lifecycle, _) = CreateLifecycle();
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        // Target A: Token credentials
        var endpointContextA = new EndpointContext { EndpointJson = """{"communicationStyle":"KubernetesApi"}""" };
        endpointContextA.SetAccountData(AccountType.Token, DeploymentAccountCredentialsConverter.Serialize(new TokenCredentials { Token = "token-a-secret" }));

        // Target B: UsernamePassword credentials
        var endpointContextB = new EndpointContext { EndpointJson = """{"communicationStyle":"KubernetesApi"}""" };
        endpointContextB.SetAccountData(AccountType.UsernamePassword, DeploymentAccountCredentialsConverter.Serialize(new UsernamePasswordCredentials { Username = "admin-b", Password = "pass-b" }));

        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("target-a", "web", transport, endpointContextA),
            MakeTarget("target-b", "web", transport, endpointContextB)
        };

        ctx.Steps = new List<DeploymentStepDto>
        {
            MakeStep("Deploy", 1, null, "web", MakeAction("DeployAction"))
        };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        strategy.Requests.Count.ShouldBe(2);

        var requestsByMachine = strategy.Requests.ToDictionary(r => r.Machine.Name, r => r);

        requestsByMachine["target-a"].EndpointContext.GetAccountData().AuthenticationAccountType.ShouldBe(AccountType.Token);
        requestsByMachine["target-b"].EndpointContext.GetAccountData().AuthenticationAccountType.ShouldBe(AccountType.UsernamePassword);
    }

    [Fact]
    public async Task Execute_ApiTarget_PropagatesEndpointContext()
    {
        var strategy = new RecordingStrategy();
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var transport = new TestTransport(strategy, CommunicationStyle.KubernetesApi);
        var (lifecycle, _) = CreateLifecycle();
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        var endpointContext = new EndpointContext { EndpointJson = """{"communicationStyle":"KubernetesApi","ClusterUrl":"https://my-cluster:6443"}""" };
        endpointContext.SetAccountData(AccountType.Token, DeploymentAccountCredentialsConverter.Serialize(new TokenCredentials { Token = "test" }));

        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("api-target", "web", transport, endpointContext)
        };

        ctx.Steps = new List<DeploymentStepDto> { MakeStep("Deploy", 1, null, "web", MakeAction("DeployAction")) };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        strategy.Requests.Count.ShouldBe(1);
        var request = strategy.Requests.Single();
        request.EndpointContext.EndpointJson.ShouldContain("my-cluster");
        request.EndpointContext.GetAccountData().AuthenticationAccountType.ShouldBe(AccountType.Token);
    }

    [Fact]
    public async Task Execute_AgentTarget_PropagatesEndpointContext()
    {
        var strategy = new RecordingStrategy();
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var transport = new TestTransport(strategy, CommunicationStyle.KubernetesAgent);
        var (lifecycle, _) = CreateLifecycle();
        var phase = new ExecuteStepsPhase(registry, lifecycle, new Mock<Squid.Core.Services.Deployments.Interruptions.IDeploymentInterruptionService>().Object, new Mock<Squid.Core.Services.Deployments.Checkpoints.IDeploymentCheckpointService>().Object, new Mock<IServerTaskService>().Object, new Mock<ITransportRegistry>().Object, new Mock<Squid.Core.Services.Deployments.ExternalFeeds.IExternalFeedDataProvider>().Object, new Mock<Squid.Core.Services.DeploymentExecution.Packages.IPackageAcquisitionService>().Object, new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser(), Squid.UnitTests.Services.Deployments.Execution.Rendering.TestIntentRendererRegistry.Create());
        var ctx = CreateBaseContext();
        lifecycle.Initialize(ctx);

        var endpointContext = new EndpointContext { EndpointJson = """{"communicationStyle":"KubernetesAgent","Namespace":"production"}""" };

        ctx.AllTargetsContext = new List<DeploymentTargetContext>
        {
            MakeTarget("agent-target", "web", transport, endpointContext)
        };

        ctx.Steps = new List<DeploymentStepDto> { MakeStep("Deploy", 1, null, "web", MakeAction("DeployAction")) };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        strategy.Requests.Count.ShouldBe(1);
        strategy.Requests.Single().EndpointContext.EndpointJson.ShouldContain("KubernetesAgent");
    }

    // ========================================================================
    // Test Doubles
    // ========================================================================

    private sealed class TestTransport : IDeploymentTransport
    {
        public TestTransport(IExecutionStrategy strategy, CommunicationStyle style)
        {
            Strategy = strategy;
            CommunicationStyle = style;
        }

        public CommunicationStyle CommunicationStyle { get; }
        public IEndpointVariableContributor Variables => null;
        public IExecutionStrategy Strategy { get; }
        public IHealthCheckStrategy HealthChecker => null;
        public ITransportCapabilities Capabilities { get; } = new TransportCapabilities();
    }

    private sealed class RecordingStrategy : IExecutionStrategy
    {
        public ConcurrentBag<ScriptExecutionRequest> Requests { get; } = new();

        public Task<ScriptExecutionResult> ExecuteScriptAsync(ScriptExecutionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new ScriptExecutionResult { Success = true, ExitCode = 0 });
        }
    }

    private sealed class SimpleRunScriptHandler : IActionHandler
    {
        public string ActionType => "Squid.Script";

        public Task<ExecutionIntent> DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct) =>
            Task.FromResult<ExecutionIntent>(new RunScriptIntent
            {
                Name = "run-script",
                ScriptBody = $"ACTION={ctx.Action.Name}",
                Syntax = ScriptSyntax.Bash
            });
    }

    // ========================================================================
    // Setup Helpers
    // ========================================================================

    private static (IDeploymentLifecycle Lifecycle, Mock<IDeploymentLogWriter> LogWriterMock) CreateLifecycle()
    {
        var logWriter = new Mock<IDeploymentLogWriter>();

        logWriter
            .Setup(x => x.AddActivityNodeAsync(It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<string>(), It.IsAny<DeploymentActivityLogNodeType>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int taskId, long? parentId, string name, DeploymentActivityLogNodeType nodeType, DeploymentActivityLogNodeStatus status, int sortOrder, CancellationToken _) =>
                new Squid.Core.Persistence.Entities.Deployments.ActivityLog { Id = Interlocked.Increment(ref _nodeId), ServerTaskId = taskId, ParentId = parentId, Name = name, NodeType = nodeType, Status = status, SortOrder = sortOrder, StartedAt = DateTimeOffset.UtcNow });

        logWriter.Setup(x => x.UpdateActivityNodeStatusAsync(It.IsAny<long>(), It.IsAny<DeploymentActivityLogNodeStatus>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        logWriter.Setup(x => x.AddLogAsync(It.IsAny<int>(), It.IsAny<long>(), It.IsAny<ServerTaskLogCategory>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        logWriter.Setup(x => x.AddLogsAsync(It.IsAny<int>(), It.IsAny<IReadOnlyCollection<ServerTaskLogWriteEntry>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        logWriter.Setup(x => x.FlushAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        logWriter.Setup(x => x.GetTreeByTaskIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ActivityLog>());

        var logger = new DeploymentActivityLogger(logWriter.Object);
        var lifecycle = new DeploymentLifecyclePublisher(new IDeploymentLifecycleHandler[] { logger });

        return (lifecycle, logWriter);
    }

    private static long _nodeId;

    private static DeploymentTaskContext CreateBaseContext()
    {
        return new DeploymentTaskContext
        {
            Task = new ServerTaskEntity { Id = 1001 },
            Deployment = new Deployment { Id = 2001, EnvironmentId = 1, ChannelId = 1 },
            Release = new ReleaseEntity { Id = 3001, Version = "1.0.0" },
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<ReleaseSelectedPackage>()
        };
    }

    private static DeploymentTargetContext MakeTarget(string name, string roles, IDeploymentTransport transport, EndpointContext endpointContext)
    {
        return new DeploymentTargetContext
        {
            Machine = new Machine { Name = name, Roles = JsonSerializer.Serialize(new[] { roles }) },
            EndpointContext = endpointContext,
            Transport = transport,
            CommunicationStyle = transport.CommunicationStyle
        };
    }

    private static DeploymentStepDto MakeStep(string name, int order, string startTrigger, string targetRoles, DeploymentActionDto action)
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
                new() { StepId = order, PropertyName = SpecialVariables.Step.TargetRoles, PropertyValue = targetRoles }
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
}
