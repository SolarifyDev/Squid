using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class ExecuteStepsPhasePackageReferencesTests : IDisposable
{
    private readonly Mock<IDeploymentLifecycle> _lifecycleMock;
    private readonly Mock<IExternalFeedDataProvider> _feedProviderMock;
    private readonly Mock<IPackageAcquisitionService> _acquisitionMock;
    private readonly Mock<IActionHandlerRegistry> _handlerRegistryMock;
    private readonly Mock<IDeploymentInterruptionService> _interruptionMock;
    private readonly Mock<IDeploymentCheckpointService> _checkpointMock;
    private readonly Mock<IServerTaskService> _taskServiceMock;
    private readonly Mock<ITransportRegistry> _transportRegistryMock;
    private readonly Mock<IExecutionStrategy> _strategyMock;
    private readonly Mock<IDeploymentTransport> _transportMock;
    private readonly Mock<IActionHandler> _actionHandlerMock;
    private readonly ExecuteStepsPhase _phase;
    private readonly DeploymentTaskContext _ctx;
    private readonly List<ScriptExecutionRequest> _capturedRequests;

    public ExecuteStepsPhasePackageReferencesTests()
    {
        _capturedRequests = new List<ScriptExecutionRequest>();
        _lifecycleMock = new Mock<IDeploymentLifecycle>();
        _feedProviderMock = new Mock<IExternalFeedDataProvider>();
        _acquisitionMock = new Mock<IPackageAcquisitionService>();
        _handlerRegistryMock = new Mock<IActionHandlerRegistry>();
        _interruptionMock = new Mock<IDeploymentInterruptionService>();
        _checkpointMock = new Mock<IDeploymentCheckpointService>();
        _taskServiceMock = new Mock<IServerTaskService>();
        _transportRegistryMock = new Mock<ITransportRegistry>();
        _strategyMock = new Mock<IExecutionStrategy>();
        _transportMock = new Mock<IDeploymentTransport>();
        _actionHandlerMock = new Mock<IActionHandler>();

        _lifecycleMock.Setup(l => l.EmitAsync(It.IsAny<DeploymentLifecycleEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _strategyMock.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScriptExecutionRequest, CancellationToken>((req, _) => _capturedRequests.Add(req))
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string>() });

        _transportMock.Setup(t => t.Strategy).Returns(_strategyMock.Object);
        _transportRegistryMock.Setup(r => r.Resolve(It.IsAny<CommunicationStyle>())).Returns(_transportMock.Object);

        _phase = new ExecuteStepsPhase(
            _handlerRegistryMock.Object,
            _lifecycleMock.Object,
            _interruptionMock.Object,
            _checkpointMock.Object,
            _taskServiceMock.Object,
            _transportRegistryMock.Object,
            _feedProviderMock.Object,
            _acquisitionMock.Object,
            new Squid.Core.Services.DeploymentExecution.Script.ServiceMessages.ServiceMessageParser());

        _ctx = new DeploymentTaskContext
        {
            ServerTaskId = 1,
            Deployment = new Deployment { Id = 1, EnvironmentId = 1, ChannelId = 1 },
            SelectedPackages = new List<ReleaseSelectedPackage>(),
            Steps = new List<DeploymentStepDto>(),
            Variables = new List<VariableDto>(),
            AcquiredPackages = new Dictionary<string, PackageAcquisitionResult>(),
            AllTargetsContext = new List<DeploymentTargetContext>()
        };
    }

    public void Dispose() { }

    private DeploymentStepDto MakeStep(string actionName) => new()
    {
        Id = 1,
        StepOrder = 1,
        Name = "Deploy Package",
        StepType = "DeployPackage",
        Condition = "Success",
        StartTrigger = "StartAfterPrevious",
        PackageRequirement = "AfterPackageAcquisition",
        Actions = new List<DeploymentActionDto>
        {
            new()
            {
                Id = 1,
                ActionOrder = 1,
                Name = actionName,
                ActionType = "Squid.Script",
                IsDisabled = false,
                Properties = new List<DeploymentActionPropertyDto>
                {
                    new() { PropertyName = "Squid.Action.Script.ScriptBody", PropertyValue = "echo 'test'" },
                    new() { PropertyName = "Squid.Action.Script.Syntax", PropertyValue = "Bash" }
                }
            }
        }
    };

    private DeploymentTargetContext MakeTargetContext() => new()
    {
        Machine = new Machine { Id = 1, Name = "target-1", Roles = "web" },
        CommunicationStyle = CommunicationStyle.Ssh,
        Transport = _transportMock.Object,
        EndpointContext = new EndpointContext()
    };

    private void SetupActionHandler(string actionName)
    {
        _handlerRegistryMock.Setup(r => r.ResolveScope(It.IsAny<DeploymentActionDto>())).Returns(ExecutionScope.TargetLevel);
        _handlerRegistryMock.Setup(r => r.Resolve(It.IsAny<DeploymentActionDto>())).Returns(_actionHandlerMock.Object);

        _actionHandlerMock.Setup(h => h.PrepareAsync(It.IsAny<ActionExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionExecutionResult
            {
                ActionName = actionName,
                ActionType = "Squid.Script",
                ScriptBody = "echo 'test'",
                Syntax = ScriptSyntax.Bash,
                ExecutionMode = ExecutionMode.DirectScript,
                Files = new Dictionary<string, byte[]>()
            });
    }

    [Fact]
    public async Task BuildPackageReferences_PackageAcquiredForAction_PopulatesPackageReferences()
    {
        const string actionName = "Deploy Web";
        _ctx.SelectedPackages = new List<ReleaseSelectedPackage>
        {
            new() { Id = 1, ReleaseId = 1, FeedId = 10, ActionName = actionName, PackageReferenceName = "nginx", Version = "1.21.0" }
        };
        _ctx.AcquiredPackages = new Dictionary<string, PackageAcquisitionResult>
        {
            ["nginx"] = new PackageAcquisitionResult("/tmp/nginx.1.21.0.nupkg", "nginx", "1.21.0", 5000, "abc123")
        };
        _ctx.Steps = new List<DeploymentStepDto> { MakeStep(actionName) };
        _ctx.AllTargetsContext = new List<DeploymentTargetContext> { MakeTargetContext() };

        SetupActionHandler(actionName);

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _capturedRequests.Count.ShouldBe(1);
        var request = _capturedRequests[0];
        request.PackageReferences.Count.ShouldBe(1);
        request.PackageReferences[0].PackageId.ShouldBe("nginx");
        request.PackageReferences[0].Version.ShouldBe("1.21.0");
        request.PackageReferences[0].LocalPath.ShouldBe("/tmp/nginx.1.21.0.nupkg");
        request.PackageReferences[0].SizeBytes.ShouldBe(5000);
        request.PackageReferences[0].Hash.ShouldBe("abc123");
    }

    [Fact]
    public async Task BuildPackageReferences_NoAcquiredPackages_ReturnsEmptyList()
    {
        const string actionName = "Deploy Web";
        _ctx.SelectedPackages = new List<ReleaseSelectedPackage>
        {
            new() { Id = 1, ReleaseId = 1, FeedId = 10, ActionName = actionName, PackageReferenceName = "nginx", Version = "1.21.0" }
        };
        _ctx.AcquiredPackages = new Dictionary<string, PackageAcquisitionResult>();
        _ctx.Steps = new List<DeploymentStepDto> { MakeStep(actionName) };
        _ctx.AllTargetsContext = new List<DeploymentTargetContext> { MakeTargetContext() };

        SetupActionHandler(actionName);

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _capturedRequests.Count.ShouldBe(1);
        _capturedRequests[0].PackageReferences.ShouldBeEmpty();
    }

    [Fact]
    public async Task BuildPackageReferences_MultiplePackagesForSameAction_AllIncluded()
    {
        const string actionName = "Deploy Web";
        _ctx.SelectedPackages = new List<ReleaseSelectedPackage>
        {
            new() { Id = 1, ReleaseId = 1, FeedId = 10, ActionName = actionName, PackageReferenceName = "nginx", Version = "1.21.0" },
            new() { Id = 2, ReleaseId = 1, FeedId = 10, ActionName = actionName, PackageReferenceName = "redis", Version = "7.0.0" }
        };
        _ctx.AcquiredPackages = new Dictionary<string, PackageAcquisitionResult>
        {
            ["nginx"] = new PackageAcquisitionResult("/tmp/nginx.1.21.0.nupkg", "nginx", "1.21.0", 5000, "abc123"),
            ["redis"] = new PackageAcquisitionResult("/tmp/redis.7.0.0.nupkg", "redis", "7.0.0", 3000, "def456")
        };
        _ctx.Steps = new List<DeploymentStepDto> { MakeStep(actionName) };
        _ctx.AllTargetsContext = new List<DeploymentTargetContext> { MakeTargetContext() };

        SetupActionHandler(actionName);

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _capturedRequests.Count.ShouldBe(1);
        var request = _capturedRequests[0];
        request.PackageReferences.Count.ShouldBe(2);
        request.PackageReferences.ShouldContain(p => p.PackageId == "nginx" && p.Version == "1.21.0");
        request.PackageReferences.ShouldContain(p => p.PackageId == "redis" && p.Version == "7.0.0");
    }

    [Fact]
    public async Task BuildPackageReferences_PackagesForDifferentActions_OnlyRelevantIncluded()
    {
        const string actionName = "Deploy Web";
        _ctx.SelectedPackages = new List<ReleaseSelectedPackage>
        {
            new() { Id = 1, ReleaseId = 1, FeedId = 10, ActionName = actionName, PackageReferenceName = "nginx", Version = "1.21.0" },
            new() { Id = 2, ReleaseId = 1, FeedId = 10, ActionName = "Deploy Database", PackageReferenceName = "postgres", Version = "15.0" }
        };
        _ctx.AcquiredPackages = new Dictionary<string, PackageAcquisitionResult>
        {
            ["nginx"] = new PackageAcquisitionResult("/tmp/nginx.1.21.0.nupkg", "nginx", "1.21.0", 5000, "abc123"),
            ["postgres"] = new PackageAcquisitionResult("/tmp/postgres.15.0.nupkg", "postgres", "15.0", 8000, "xyz789")
        };
        _ctx.Steps = new List<DeploymentStepDto> { MakeStep(actionName) };
        _ctx.AllTargetsContext = new List<DeploymentTargetContext> { MakeTargetContext() };

        SetupActionHandler(actionName);

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _capturedRequests.Count.ShouldBe(1);
        var request = _capturedRequests[0];
        request.PackageReferences.Count.ShouldBe(1);
        request.PackageReferences[0].PackageId.ShouldBe("nginx");
        request.PackageReferences.ShouldNotContain(p => p.PackageId == "postgres");
    }

    [Fact]
    public async Task BuildPackageReferences_CaseInsensitiveActionNameMatching()
    {
        const string actionName = "Deploy Web";
        _ctx.SelectedPackages = new List<ReleaseSelectedPackage>
        {
            new() { Id = 1, ReleaseId = 1, FeedId = 10, ActionName = "DEPLOY WEB", PackageReferenceName = "nginx", Version = "1.21.0" }
        };
        _ctx.AcquiredPackages = new Dictionary<string, PackageAcquisitionResult>
        {
            ["nginx"] = new PackageAcquisitionResult("/tmp/nginx.1.21.0.nupkg", "nginx", "1.21.0", 5000, "abc123")
        };
        _ctx.Steps = new List<DeploymentStepDto> { MakeStep(actionName) };
        _ctx.AllTargetsContext = new List<DeploymentTargetContext> { MakeTargetContext() };

        SetupActionHandler(actionName);

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _capturedRequests.Count.ShouldBe(1);
        _capturedRequests[0].PackageReferences.Count.ShouldBe(1);
        _capturedRequests[0].PackageReferences[0].PackageId.ShouldBe("nginx");
    }

    [Fact]
    public async Task BuildPackageReferences_NoSelectedPackages_ReturnsEmptyList()
    {
        const string actionName = "Deploy Web";
        _ctx.SelectedPackages = new List<ReleaseSelectedPackage>();
        _ctx.AcquiredPackages = new Dictionary<string, PackageAcquisitionResult>
        {
            ["nginx"] = new PackageAcquisitionResult("/tmp/nginx.1.21.0.nupkg", "nginx", "1.21.0", 5000, "abc123")
        };
        _ctx.Steps = new List<DeploymentStepDto> { MakeStep(actionName) };
        _ctx.AllTargetsContext = new List<DeploymentTargetContext> { MakeTargetContext() };

        SetupActionHandler(actionName);

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _capturedRequests.Count.ShouldBe(1);
        _capturedRequests[0].PackageReferences.ShouldBeEmpty();
    }

    [Fact]
    public async Task BuildPackageReferences_OneAcquiredPackageTwoActions_OnlyMatchingActionGetsPackage()
    {
        const string action1Name = "Deploy Web";
        const string action2Name = "Deploy Database";

        _ctx.SelectedPackages = new List<ReleaseSelectedPackage>
        {
            new() { Id = 1, ReleaseId = 1, FeedId = 10, ActionName = action1Name, PackageReferenceName = "nginx", Version = "1.21.0" }
        };
        _ctx.AcquiredPackages = new Dictionary<string, PackageAcquisitionResult>
        {
            ["nginx"] = new PackageAcquisitionResult("/tmp/nginx.1.21.0.nupkg", "nginx", "1.21.0", 5000, "abc123")
        };

        var step1 = MakeStep(action1Name);
        step1.StepOrder = 1;
        var step2 = MakeStep(action2Name);
        step2.Id = 2;
        step2.StepOrder = 2;
        step2.Actions[0].Id = 2;

        _ctx.Steps = new List<DeploymentStepDto> { step1, step2 };
        _ctx.AllTargetsContext = new List<DeploymentTargetContext> { MakeTargetContext() };

        _handlerRegistryMock.Setup(r => r.ResolveScope(It.IsAny<DeploymentActionDto>())).Returns(ExecutionScope.TargetLevel);
        _handlerRegistryMock.Setup(r => r.Resolve(It.IsAny<DeploymentActionDto>())).Returns(_actionHandlerMock.Object);

        _actionHandlerMock.Setup(h => h.PrepareAsync(It.Is<ActionExecutionContext>(ctx => ctx.Action.Name == action1Name), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionExecutionResult
            {
                ActionName = action1Name,
                ActionType = "Squid.Script",
                ScriptBody = "echo 'web'",
                Syntax = ScriptSyntax.Bash,
                ExecutionMode = ExecutionMode.DirectScript,
                Files = new Dictionary<string, byte[]>()
            });

        _actionHandlerMock.Setup(h => h.PrepareAsync(It.Is<ActionExecutionContext>(ctx => ctx.Action.Name == action2Name), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActionExecutionResult
            {
                ActionName = action2Name,
                ActionType = "Squid.Script",
                ScriptBody = "echo 'db'",
                Syntax = ScriptSyntax.Bash,
                ExecutionMode = ExecutionMode.DirectScript,
                Files = new Dictionary<string, byte[]>()
            });

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _capturedRequests.Count.ShouldBe(2);

        var request1 = _capturedRequests[0];
        request1.ActionName.ShouldBe(action1Name);
        request1.PackageReferences.Count.ShouldBe(1);
        request1.PackageReferences[0].PackageId.ShouldBe("nginx");

        var request2 = _capturedRequests[1];
        request2.ActionName.ShouldBe(action2Name);
        request2.PackageReferences.ShouldBeEmpty();
    }

    [Fact]
    public async Task BuildPackageReferences_MultipleTargets_EachGetsPackageReferences()
    {
        const string actionName = "Deploy Web";
        _ctx.SelectedPackages = new List<ReleaseSelectedPackage>
        {
            new() { Id = 1, ReleaseId = 1, FeedId = 10, ActionName = actionName, PackageReferenceName = "nginx", Version = "1.21.0" }
        };
        _ctx.AcquiredPackages = new Dictionary<string, PackageAcquisitionResult>
        {
            ["nginx"] = new PackageAcquisitionResult("/tmp/nginx.1.21.0.nupkg", "nginx", "1.21.0", 5000, "abc123")
        };
        _ctx.Steps = new List<DeploymentStepDto> { MakeStep(actionName) };

        var target1 = MakeTargetContext();
        target1.Machine.Name = "target-1";
        var target2 = MakeTargetContext();
        target2.Machine.Id = 2;
        target2.Machine.Name = "target-2";

        _ctx.AllTargetsContext = new List<DeploymentTargetContext> { target1, target2 };

        SetupActionHandler(actionName);

        await _phase.ExecuteAsync(_ctx, CancellationToken.None);

        _capturedRequests.Count.ShouldBe(2);
        _capturedRequests[0].PackageReferences.Count.ShouldBe(1);
        _capturedRequests[0].PackageReferences[0].PackageId.ShouldBe("nginx");
        _capturedRequests[1].PackageReferences.Count.ShouldBe(1);
        _capturedRequests[1].PackageReferences[0].PackageId.ShouldBe("nginx");
    }
}
