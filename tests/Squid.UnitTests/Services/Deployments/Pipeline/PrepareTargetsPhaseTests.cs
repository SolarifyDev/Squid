using System.Linq;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Deployments.Account.Exceptions;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;

namespace Squid.UnitTests.Services.Deployments.Pipeline;

public class PrepareTargetsPhaseTests
{
    private readonly Mock<ITransportRegistry> _transportRegistry = new();
    private readonly Mock<IEndpointContextBuilder> _endpointContextBuilder = new();
    private readonly Mock<IDeploymentAccountDataProvider> _accountDataProvider = new();
    private readonly PrepareTargetsPhase _phase;

    public PrepareTargetsPhaseTests()
    {
        _phase = new PrepareTargetsPhase(_transportRegistry.Object, _endpointContextBuilder.Object, _accountDataProvider.Object);
    }

    // ========================================================================
    // Transport Resolution
    // ========================================================================

    [Fact]
    public async Task Execute_KnownStyle_ResolvesTransport()
    {
        var transport = CreateMockTransport(CommunicationStyle.KubernetesApi);
        var endpointJson = MakeEndpointJson("KubernetesApi");
        var expectedContext = new EndpointContext { EndpointJson = endpointJson };

        _transportRegistry.Setup(r => r.Resolve(CommunicationStyle.KubernetesApi)).Returns(transport.Object);
        _endpointContextBuilder.Setup(b => b.BuildAsync(endpointJson, transport.Object.Variables, It.IsAny<CancellationToken>())).ReturnsAsync(expectedContext);

        var ctx = CreateContext(MakeMachine("target-1", endpointJson));

        await _phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.AllTargetsContext.Count.ShouldBe(1);
        ctx.AllTargetsContext[0].Transport.ShouldBe(transport.Object);
        ctx.AllTargetsContext[0].CommunicationStyle.ShouldBe(CommunicationStyle.KubernetesApi);
        ctx.AllTargetsContext[0].EndpointContext.ShouldBe(expectedContext);
    }

    [Fact]
    public async Task Execute_UnknownStyle_SetsMinimalEndpointContext()
    {
        var endpointJson = """{"communicationStyle":"SomeUnknown"}""";
        _transportRegistry.Setup(r => r.Resolve(CommunicationStyle.Unknown)).Returns((IDeploymentTransport)null);

        var ctx = CreateContext(MakeMachine("target-1", endpointJson));

        await _phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.AllTargetsContext.Count.ShouldBe(1);
        ctx.AllTargetsContext[0].Transport.ShouldBeNull();
        ctx.AllTargetsContext[0].CommunicationStyle.ShouldBe(CommunicationStyle.Unknown);
        ctx.AllTargetsContext[0].EndpointContext.EndpointJson.ShouldBe(endpointJson);
    }

    [Fact]
    public async Task Execute_NullEndpoint_SetsUnknownStyle()
    {
        _transportRegistry.Setup(r => r.Resolve(CommunicationStyle.Unknown)).Returns((IDeploymentTransport)null);

        var ctx = CreateContext(MakeMachine("target-1", null));

        await _phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.AllTargetsContext.Count.ShouldBe(1);
        ctx.AllTargetsContext[0].CommunicationStyle.ShouldBe(CommunicationStyle.Unknown);
        ctx.AllTargetsContext[0].Transport.ShouldBeNull();
    }

    // ========================================================================
    // EndpointContextBuilder Integration
    // ========================================================================

    [Fact]
    public async Task Execute_WithTransport_CallsEndpointContextBuilder()
    {
        var endpointJson = MakeEndpointJson("KubernetesApi");
        var transport = CreateMockTransport(CommunicationStyle.KubernetesApi);
        var expectedContext = new EndpointContext { EndpointJson = endpointJson };

        _transportRegistry.Setup(r => r.Resolve(CommunicationStyle.KubernetesApi)).Returns(transport.Object);
        _endpointContextBuilder.Setup(b => b.BuildAsync(endpointJson, transport.Object.Variables, It.IsAny<CancellationToken>())).ReturnsAsync(expectedContext);

        var ctx = CreateContext(MakeMachine("target-1", endpointJson));

        await _phase.ExecuteAsync(ctx, CancellationToken.None);

        _endpointContextBuilder.Verify(b => b.BuildAsync(endpointJson, transport.Object.Variables, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_WithoutTransport_DoesNotCallBuilder()
    {
        var endpointJson = """{"communicationStyle":"SomeUnknown"}""";
        _transportRegistry.Setup(r => r.Resolve(It.IsAny<CommunicationStyle>())).Returns((IDeploymentTransport)null);

        var ctx = CreateContext(MakeMachine("target-1", endpointJson));

        await _phase.ExecuteAsync(ctx, CancellationToken.None);

        _endpointContextBuilder.Verify(b => b.BuildAsync(It.IsAny<string>(), It.IsAny<IEndpointVariableContributor>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Execute_BuilderResult_SetOnTargetContext()
    {
        var endpointJson = MakeEndpointJson("KubernetesApi");
        var transport = CreateMockTransport(CommunicationStyle.KubernetesApi);
        var expectedContext = new EndpointContext { EndpointJson = endpointJson };
        expectedContext.SetAccountData(AccountType.Token, """{"token":"test"}""");

        _transportRegistry.Setup(r => r.Resolve(CommunicationStyle.KubernetesApi)).Returns(transport.Object);
        _endpointContextBuilder.Setup(b => b.BuildAsync(endpointJson, transport.Object.Variables, It.IsAny<CancellationToken>())).ReturnsAsync(expectedContext);

        var ctx = CreateContext(MakeMachine("target-1", endpointJson));

        await _phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.AllTargetsContext[0].EndpointContext.GetAccountData().ShouldNotBeNull();
        ctx.AllTargetsContext[0].EndpointContext.GetAccountData().AuthenticationAccountType.ShouldBe(AccountType.Token);
    }

    // ========================================================================
    // Account Environment Scope Validation
    // ========================================================================

    [Fact]
    public async Task Execute_AccountInScope_Succeeds()
    {
        var endpointJson = MakeEndpointJsonWithAccount("KubernetesApi", 10);
        var transport = CreateMockTransportWithRefs(CommunicationStyle.KubernetesApi, endpointJson, accountId: 10);

        _transportRegistry.Setup(r => r.Resolve(CommunicationStyle.KubernetesApi)).Returns(transport.Object);
        _endpointContextBuilder.Setup(b => b.BuildAsync(endpointJson, transport.Object.Variables, It.IsAny<CancellationToken>())).ReturnsAsync(new EndpointContext { EndpointJson = endpointJson });

        _accountDataProvider.Setup(a => a.GetAccountByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentAccount { Id = 10, Name = "Test", EnvironmentIds = "[1,3]" });

        var ctx = CreateContext(MakeMachine("target-1", endpointJson), environmentId: 3);

        await _phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.AllTargetsContext.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Execute_AccountOutOfScope_ThrowsAccountEnvironmentScopeException()
    {
        var endpointJson = MakeEndpointJsonWithAccount("KubernetesApi", 10);
        var transport = CreateMockTransportWithRefs(CommunicationStyle.KubernetesApi, endpointJson, accountId: 10);

        _transportRegistry.Setup(r => r.Resolve(CommunicationStyle.KubernetesApi)).Returns(transport.Object);
        _endpointContextBuilder.Setup(b => b.BuildAsync(endpointJson, transport.Object.Variables, It.IsAny<CancellationToken>())).ReturnsAsync(new EndpointContext { EndpointJson = endpointJson });

        _accountDataProvider.Setup(a => a.GetAccountByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentAccount { Id = 10, Name = "Test", EnvironmentIds = "[1,3]" });

        var ctx = CreateContext(MakeMachine("target-1", endpointJson), environmentId: 99);

        await Should.ThrowAsync<AccountEnvironmentScopeException>(() => _phase.ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task Execute_NoAccountReference_SkipsValidation()
    {
        var endpointJson = MakeEndpointJson("KubernetesApi");
        var transport = CreateMockTransportWithRefs(CommunicationStyle.KubernetesApi, endpointJson, accountId: null);

        _transportRegistry.Setup(r => r.Resolve(CommunicationStyle.KubernetesApi)).Returns(transport.Object);
        _endpointContextBuilder.Setup(b => b.BuildAsync(endpointJson, transport.Object.Variables, It.IsAny<CancellationToken>())).ReturnsAsync(new EndpointContext { EndpointJson = endpointJson });

        var ctx = CreateContext(MakeMachine("target-1", endpointJson));

        await _phase.ExecuteAsync(ctx, CancellationToken.None);

        _accountDataProvider.Verify(a => a.GetAccountByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Execute_AccountNotFoundForScope_SkipsValidation()
    {
        var endpointJson = MakeEndpointJsonWithAccount("KubernetesApi", 10);
        var transport = CreateMockTransportWithRefs(CommunicationStyle.KubernetesApi, endpointJson, accountId: 10);

        _transportRegistry.Setup(r => r.Resolve(CommunicationStyle.KubernetesApi)).Returns(transport.Object);
        _endpointContextBuilder.Setup(b => b.BuildAsync(endpointJson, transport.Object.Variables, It.IsAny<CancellationToken>())).ReturnsAsync(new EndpointContext { EndpointJson = endpointJson });

        _accountDataProvider.Setup(a => a.GetAccountByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync((DeploymentAccount)null);

        var ctx = CreateContext(MakeMachine("target-1", endpointJson));

        await _phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.AllTargetsContext.Count.ShouldBe(1);
    }

    // ========================================================================
    // Variable Contribution
    // ========================================================================

    [Fact]
    public async Task Execute_ContributeEndpointVariables_AddsToTargetContext()
    {
        var endpointJson = MakeEndpointJson("KubernetesApi");
        var transport = CreateMockTransport(CommunicationStyle.KubernetesApi);
        var expectedVars = new List<VariableDto> { new() { Name = "Squid.Account.Token", Value = "test-token" } };
        transport.Setup(t => t.Variables.ContributeVariables(It.IsAny<EndpointContext>())).Returns(expectedVars);

        _transportRegistry.Setup(r => r.Resolve(CommunicationStyle.KubernetesApi)).Returns(transport.Object);
        _endpointContextBuilder.Setup(b => b.BuildAsync(endpointJson, transport.Object.Variables, It.IsAny<CancellationToken>())).ReturnsAsync(new EndpointContext { EndpointJson = endpointJson });

        var ctx = CreateContext(MakeMachine("target-1", endpointJson));

        await _phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.AllTargetsContext[0].EndpointVariables.ShouldContain(v => v.Name == "Squid.Account.Token");
    }

    [Fact]
    public async Task Execute_NullTransport_SkipsVariableContribution()
    {
        var endpointJson = """{"communicationStyle":"SomeUnknown"}""";
        _transportRegistry.Setup(r => r.Resolve(It.IsAny<CommunicationStyle>())).Returns((IDeploymentTransport)null);

        var ctx = CreateContext(MakeMachine("target-1", endpointJson));

        await _phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.AllTargetsContext[0].EndpointVariables.ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_ContributeAdditionalVariables_Appended()
    {
        var endpointJson = MakeEndpointJson("KubernetesApi");
        var transport = CreateMockTransport(CommunicationStyle.KubernetesApi);
        var baseVars = new List<VariableDto> { new() { Name = "Squid.Account.Token", Value = "t" } };
        var additionalVars = new List<VariableDto> { new() { Name = "ContainerImage", Value = "docker.io/nginx:1.0" } };

        transport.Setup(t => t.Variables.ContributeVariables(It.IsAny<EndpointContext>())).Returns(baseVars);
        transport.Setup(t => t.Variables.ContributeAdditionalVariablesAsync(It.IsAny<DeploymentProcessSnapshotDto>(), It.IsAny<ReleaseEntity>(), It.IsAny<CancellationToken>())).ReturnsAsync(additionalVars);

        _transportRegistry.Setup(r => r.Resolve(CommunicationStyle.KubernetesApi)).Returns(transport.Object);
        _endpointContextBuilder.Setup(b => b.BuildAsync(endpointJson, transport.Object.Variables, It.IsAny<CancellationToken>())).ReturnsAsync(new EndpointContext { EndpointJson = endpointJson });

        var ctx = CreateContext(MakeMachine("target-1", endpointJson));

        await _phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.AllTargetsContext[0].EndpointVariables.ShouldContain(v => v.Name == "Squid.Account.Token");
        ctx.AllTargetsContext[0].EndpointVariables.ShouldContain(v => v.Name == "ContainerImage");
    }

    // ========================================================================
    // Multi-Target
    // ========================================================================

    [Fact]
    public async Task Execute_MultipleTargets_EachGetsOwnContext()
    {
        var endpointJsonA = MakeEndpointJson("KubernetesApi", "https://a.example.com");
        var endpointJsonB = MakeEndpointJson("KubernetesApi", "https://b.example.com");
        var transport = CreateMockTransport(CommunicationStyle.KubernetesApi);

        _transportRegistry.Setup(r => r.Resolve(CommunicationStyle.KubernetesApi)).Returns(transport.Object);
        _endpointContextBuilder.Setup(b => b.BuildAsync(endpointJsonA, transport.Object.Variables, It.IsAny<CancellationToken>())).ReturnsAsync(new EndpointContext { EndpointJson = endpointJsonA });
        _endpointContextBuilder.Setup(b => b.BuildAsync(endpointJsonB, transport.Object.Variables, It.IsAny<CancellationToken>())).ReturnsAsync(new EndpointContext { EndpointJson = endpointJsonB });

        var ctx = CreateContext(MakeMachine("target-a", endpointJsonA), MakeMachine("target-b", endpointJsonB));

        await _phase.ExecuteAsync(ctx, CancellationToken.None);

        ctx.AllTargetsContext.Count.ShouldBe(2);
        ctx.AllTargetsContext[0].EndpointContext.EndpointJson.ShouldBe(endpointJsonA);
        ctx.AllTargetsContext[1].EndpointContext.EndpointJson.ShouldBe(endpointJsonB);
        _endpointContextBuilder.Verify(b => b.BuildAsync(It.IsAny<string>(), It.IsAny<IEndpointVariableContributor>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Execute_MultipleTargets_OneFailsScope_ThrowsOnFirst()
    {
        var endpointJsonA = MakeEndpointJsonWithAccount("KubernetesApi", 10);
        var endpointJsonB = MakeEndpointJsonWithAccount("KubernetesApi", 20);
        var transport = CreateMockTransport(CommunicationStyle.KubernetesApi);

        // Set up refs for both endpoints
        transport.Setup(t => t.Variables.ParseResourceReferences(endpointJsonA))
            .Returns(new EndpointResourceReferences
            {
                References = new List<EndpointResourceReference>
                    { new() { Type = EndpointResourceType.AuthenticationAccount, ResourceId = 10 } }
            });
        transport.Setup(t => t.Variables.ParseResourceReferences(endpointJsonB))
            .Returns(new EndpointResourceReferences
            {
                References = new List<EndpointResourceReference>
                    { new() { Type = EndpointResourceType.AuthenticationAccount, ResourceId = 20 } }
            });

        _transportRegistry.Setup(r => r.Resolve(CommunicationStyle.KubernetesApi)).Returns(transport.Object);
        _endpointContextBuilder.Setup(b => b.BuildAsync(It.IsAny<string>(), transport.Object.Variables, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string ep, IEndpointVariableContributor _, CancellationToken _) => new EndpointContext { EndpointJson = ep });

        // First account in scope, second out of scope
        _accountDataProvider.Setup(a => a.GetAccountByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentAccount { Id = 10, Name = "InScope", EnvironmentIds = "[1]" });
        _accountDataProvider.Setup(a => a.GetAccountByIdAsync(20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeploymentAccount { Id = 20, Name = "OutScope", EnvironmentIds = "[5]" });

        var ctx = CreateContext(MakeMachine("target-a", endpointJsonA), MakeMachine("target-b", endpointJsonB));
        ctx.Environment = new Environment { Id = 1 };

        await Should.ThrowAsync<AccountEnvironmentScopeException>(() => _phase.ExecuteAsync(ctx, CancellationToken.None));

        // First target should have been fully processed before second fails
        ctx.AllTargetsContext.Count.ShouldBe(1);
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static string MakeEndpointJson(string style, string clusterUrl = "https://localhost:6443")
    {
        return JsonSerializer.Serialize(new { communicationStyle = style, ClusterUrl = clusterUrl, Namespace = "default" });
    }

    private static string MakeEndpointJsonWithAccount(string style, int accountId)
    {
        return JsonSerializer.Serialize(new
        {
            communicationStyle = style,
            ClusterUrl = "https://localhost:6443",
            Namespace = "default",
            ResourceReferences = new[] { new { Type = (int)EndpointResourceType.AuthenticationAccount, ResourceId = accountId } }
        });
    }

    private static Machine MakeMachine(string name, string endpointJson)
    {
        return new Machine { Id = name.GetHashCode(), Name = name, Endpoint = endpointJson };
    }

    private static DeploymentTaskContext CreateContext(params Machine[] machines)
    {
        return CreateContext(1, machines);
    }

    private static DeploymentTaskContext CreateContext(Machine machine, int environmentId)
    {
        return CreateContext(environmentId, machine);
    }

    private static DeploymentTaskContext CreateContext(int environmentId, params Machine[] machines)
    {
        return new DeploymentTaskContext
        {
            Task = new ServerTaskEntity { Id = 1 },
            Deployment = new Deployment { Id = 1, EnvironmentId = environmentId },
            Release = new ReleaseEntity { Id = 1, Version = "1.0.0" },
            Environment = new Environment { Id = environmentId },
            Variables = new List<VariableDto>(),
            AllTargets = machines.ToList()
        };
    }

    private static Mock<IDeploymentTransport> CreateMockTransport(CommunicationStyle style)
    {
        var variableContributor = new Mock<IEndpointVariableContributor>();
        variableContributor.Setup(v => v.ParseResourceReferences(It.IsAny<string>())).Returns(new EndpointResourceReferences());
        variableContributor.Setup(v => v.ContributeVariables(It.IsAny<EndpointContext>())).Returns(new List<VariableDto>());
        variableContributor.Setup(v => v.ContributeAdditionalVariablesAsync(It.IsAny<DeploymentProcessSnapshotDto>(), It.IsAny<ReleaseEntity>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<VariableDto>());

        var transport = new Mock<IDeploymentTransport>();
        transport.Setup(t => t.CommunicationStyle).Returns(style);
        transport.Setup(t => t.Variables).Returns(variableContributor.Object);

        return transport;
    }

    private static Mock<IDeploymentTransport> CreateMockTransportWithRefs(CommunicationStyle style, string endpointJson, int? accountId)
    {
        var transport = CreateMockTransport(style);

        var refs = new EndpointResourceReferences();
        if (accountId.HasValue)
            refs.References.Add(new EndpointResourceReference { Type = EndpointResourceType.AuthenticationAccount, ResourceId = accountId.Value });

        transport.Setup(t => t.Variables.ParseResourceReferences(endpointJson)).Returns(refs);

        return transport;
    }
}
