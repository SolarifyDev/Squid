using System.Collections.Concurrent;
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

        var wrapperA = new CredentialCapturingWrapper("target-a");
        var wrapperB = new CredentialCapturingWrapper("target-b");

        var transportA = new TestTransport(strategy, wrapperA, CommunicationStyle.KubernetesApi);
        var transportB = new TestTransport(strategy, wrapperB, CommunicationStyle.KubernetesApi);

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
            MakeTarget("target-a", "web", transportA, endpointContextA),
            MakeTarget("target-b", "web", transportB, endpointContextB)
        };

        ctx.Steps = new List<DeploymentStepDto>
        {
            MakeStep("Deploy", 1, null, "web", MakeAction("DeployAction"))
        };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        strategy.Requests.Count.ShouldBe(2);

        // Verify credential isolation — each target wrapper received its own endpoint context
        wrapperA.CapturedEndpointJson.ShouldNotBeNull();
        wrapperB.CapturedEndpointJson.ShouldNotBeNull();

        var accountA = wrapperA.CapturedAccountType;
        var accountB = wrapperB.CapturedAccountType;

        accountA.ShouldBe(AccountType.Token);
        accountB.ShouldBe(AccountType.UsernamePassword);
    }

    [Fact]
    public async Task Execute_ApiTarget_WrapsWithEndpointContext()
    {
        var strategy = new RecordingStrategy();
        var wrapper = new CredentialCapturingWrapper("api-target");
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var transport = new TestTransport(strategy, wrapper, CommunicationStyle.KubernetesApi);
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

        wrapper.CapturedEndpointJson.ShouldContain("my-cluster");
    }

    [Fact]
    public async Task Execute_AgentTarget_WrapsWithNamespaceOnly()
    {
        var strategy = new RecordingStrategy();
        var wrapper = new CredentialCapturingWrapper("agent-target");
        var handler = new SimpleRunScriptHandler();
        var registry = Mock.Of<IActionHandlerRegistry>(r => r.Resolve(It.IsAny<DeploymentActionDto>()) == handler);

        var transport = new TestTransport(strategy, wrapper, CommunicationStyle.KubernetesAgent);
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

        wrapper.CapturedEndpointJson.ShouldContain("KubernetesAgent");
    }

    // ========================================================================
    // Test Doubles
    // ========================================================================

    private sealed class CredentialCapturingWrapper : IScriptContextWrapper
    {
        private readonly string _expectedMachine;

        public string CapturedEndpointJson { get; private set; }
        public AccountType? CapturedAccountType { get; private set; }

        public CredentialCapturingWrapper(string expectedMachine) => _expectedMachine = expectedMachine;

        public string WrapScript(string script, ScriptContext context)
        {
            CapturedEndpointJson = context?.Endpoint?.EndpointJson;
            CapturedAccountType = context?.Endpoint?.GetAccountData()?.AuthenticationAccountType;
            return $"WRAPPED_{_expectedMachine};{script}";
        }
    }

    private sealed class TestTransport : IDeploymentTransport
    {
        public TestTransport(IExecutionStrategy strategy, IScriptContextWrapper scriptWrapper, CommunicationStyle style)
        {
            Strategy = strategy;
            ScriptWrapper = scriptWrapper;
            CommunicationStyle = style;
        }

        public CommunicationStyle CommunicationStyle { get; }
        public IEndpointVariableContributor Variables => null;
        public IScriptContextWrapper ScriptWrapper { get; }
        public IExecutionStrategy Strategy { get; }
        public IHealthCheckStrategy HealthChecker => null;
        public ExecutionLocation ExecutionLocation => ExecutionLocation.Unspecified;
        public ExecutionBackend ExecutionBackend => ExecutionBackend.Unspecified;
        public bool RequiresContextPreparationForPackagedPayload => false;
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
