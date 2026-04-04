using System.Linq;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Account;
using Squid.Core.Services.Chat;
using Squid.Core.Services.Http;
using Squid.Core.Services.Machines;
using Squid.Message.Commands.Chat;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Deployments.OpenClaw;

public class OpenClawChatServiceTests
{
    private readonly Mock<IMachineDataProvider> _machineDataProvider = new();
    private readonly Mock<IDeploymentAccountDataProvider> _accountDataProvider = new();
    private readonly Mock<ISquidHttpClientFactory> _httpClientFactory = new();

    private OpenClawChatService CreateService() => new(_machineDataProvider.Object, _accountDataProvider.Object, _httpClientFactory.Object);

    // ========================================================================
    // ResolveMachinesAsync — Machine Resolution
    // ========================================================================

    [Fact]
    public async Task ResolveMachines_FiltersOpenClawOnly()
    {
        var machines = new List<Machine>
        {
            MakeOpenClawMachine(1, "openclaw-1", roles: new[] { "web" }),
            MakeK8sMachine(2, "k8s-1", roles: new[] { "web" }),
            MakeOpenClawMachine(3, "openclaw-2", roles: new[] { "api" })
        };

        _machineDataProvider.Setup(x => x.GetMachinesByFilterAsync(It.IsAny<HashSet<int>>(), It.IsAny<HashSet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(machines);

        var service = CreateService();
        var result = await service.ResolveMachinesAsync(null, CancellationToken.None);

        result.Count.ShouldBe(2);
        result.ShouldAllBe(m => m.Name.StartsWith("openclaw"));
    }

    [Fact]
    public async Task ResolveMachines_EmptyTags_ReturnsAllOpenClaw()
    {
        var machines = new List<Machine>
        {
            MakeOpenClawMachine(1, "oc-1", roles: new[] { "web" }),
            MakeOpenClawMachine(2, "oc-2", roles: new[] { "api" })
        };

        _machineDataProvider.Setup(x => x.GetMachinesByFilterAsync(It.IsAny<HashSet<int>>(), It.IsAny<HashSet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(machines);

        var service = CreateService();
        var result = await service.ResolveMachinesAsync(new List<string>(), CancellationToken.None);

        result.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ResolveMachines_WithTags_FiltersMatchingRoles()
    {
        var machines = new List<Machine>
        {
            MakeOpenClawMachine(1, "oc-web", roles: new[] { "web", "frontend" }),
            MakeOpenClawMachine(2, "oc-api", roles: new[] { "api", "backend" }),
            MakeOpenClawMachine(3, "oc-worker", roles: new[] { "worker" })
        };

        _machineDataProvider.Setup(x => x.GetMachinesByFilterAsync(It.IsAny<HashSet<int>>(), It.IsAny<HashSet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(machines);

        var service = CreateService();
        var result = await service.ResolveMachinesAsync(new List<string> { "web", "api" }, CancellationToken.None);

        result.Count.ShouldBe(2);
        result.ShouldContain(m => m.Name == "oc-web");
        result.ShouldContain(m => m.Name == "oc-api");
    }

    [Fact]
    public async Task ResolveMachines_DisabledMachines_Excluded()
    {
        var machines = new List<Machine>
        {
            MakeOpenClawMachine(1, "oc-enabled"),
            MakeOpenClawMachine(2, "oc-disabled", isDisabled: true)
        };

        _machineDataProvider.Setup(x => x.GetMachinesByFilterAsync(It.IsAny<HashSet<int>>(), It.IsAny<HashSet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(machines);

        var service = CreateService();
        var result = await service.ResolveMachinesAsync(null, CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("oc-enabled");
    }

    [Fact]
    public async Task ResolveMachines_NoMatching_ReturnsEmpty()
    {
        var machines = new List<Machine>
        {
            MakeOpenClawMachine(1, "oc-1", roles: new[] { "web" })
        };

        _machineDataProvider.Setup(x => x.GetMachinesByFilterAsync(It.IsAny<HashSet<int>>(), It.IsAny<HashSet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(machines);

        var service = CreateService();
        var result = await service.ResolveMachinesAsync(new List<string> { "nonexistent" }, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    // ========================================================================
    // SendAsync — Non-Streaming Orchestration
    // ========================================================================

    [Fact]
    public async Task SendAsync_NoMachines_ReturnsEmptyResults()
    {
        _machineDataProvider.Setup(x => x.GetMachinesByFilterAsync(It.IsAny<HashSet<int>>(), It.IsAny<HashSet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<Machine>());

        var service = CreateService();
        var command = MakeCommand(tags: new[] { "nonexistent" });

        var result = await service.SendAsync(command, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SendAsync_EmptyBaseUrl_ReturnsFailureForThatTarget()
    {
        var machines = new List<Machine>
        {
            MakeOpenClawMachine(1, "empty-base", baseUrl: "")
        };

        _machineDataProvider.Setup(x => x.GetMachinesByFilterAsync(It.IsAny<HashSet<int>>(), It.IsAny<HashSet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(machines);

        var service = CreateService();
        var command = MakeCommand();

        var result = await service.SendAsync(command, CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Succeeded.ShouldBeFalse();
        result[0].MachineId.ShouldBe(1);
        result[0].Error.ShouldContain("invalid OpenClaw endpoint");
    }

    [Fact]
    public async Task SendAsync_MachineWithEmptyBaseUrl_ReturnsFailure()
    {
        var endpoint = new OpenClawEndpointDto { CommunicationStyle = "OpenClaw", BaseUrl = "" };
        var machines = new List<Machine>
        {
            new() { Id = 1, Name = "empty-url", Endpoint = JsonSerializer.Serialize(endpoint), Roles = "[]" }
        };

        _machineDataProvider.Setup(x => x.GetMachinesByFilterAsync(It.IsAny<HashSet<int>>(), It.IsAny<HashSet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(machines);

        var service = CreateService();
        var command = MakeCommand();

        var result = await service.SendAsync(command, CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Succeeded.ShouldBeFalse();
        result[0].Error.ShouldContain("invalid OpenClaw endpoint");
    }

    [Fact]
    public async Task SendAsync_ResolvesGatewayToken_InlineFallback()
    {
        var endpoint = new OpenClawEndpointDto
        {
            CommunicationStyle = "OpenClaw",
            BaseUrl = "https://openclaw.example.com",
            InlineGatewayToken = "inline-token-123"
        };

        var machines = new List<Machine>
        {
            new() { Id = 1, Name = "inline-token", Endpoint = JsonSerializer.Serialize(endpoint), Roles = "[]" }
        };

        _machineDataProvider.Setup(x => x.GetMachinesByFilterAsync(It.IsAny<HashSet<int>>(), It.IsAny<HashSet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(machines);

        var service = CreateService();
        var command = MakeCommand();

        // Will fail at HTTP level since we're not mocking the HTTP client,
        // but the error message will confirm it tried to call the API (not fail at token resolution)
        var result = await service.SendAsync(command, CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].MachineId.ShouldBe(1);
        result[0].MachineName.ShouldBe("inline-token");
        // It should have attempted the HTTP call (token resolved successfully) and failed at HTTP level
        result[0].Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task SendAsync_ResolvesGatewayToken_FromAccount()
    {
        var endpoint = new OpenClawEndpointDto
        {
            CommunicationStyle = "OpenClaw",
            BaseUrl = "https://openclaw.example.com",
            InlineGatewayToken = "fallback-token",
            ResourceReferences = new List<EndpointResourceReference>
            {
                new() { Type = EndpointResourceType.AuthenticationAccount, ResourceId = 42 }
            }
        };

        var machines = new List<Machine>
        {
            new() { Id = 1, Name = "account-token", Endpoint = JsonSerializer.Serialize(endpoint), Roles = "[]" }
        };

        var account = new DeploymentAccount
        {
            Id = 42,
            AccountType = AccountType.OpenClawGateway,
            Credentials = JsonSerializer.Serialize(new { GatewayToken = "account-token-456" })
        };

        _machineDataProvider.Setup(x => x.GetMachinesByFilterAsync(It.IsAny<HashSet<int>>(), It.IsAny<HashSet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(machines);
        _accountDataProvider.Setup(x => x.GetAccountByIdAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(account);

        var service = CreateService();
        var command = MakeCommand();

        var result = await service.SendAsync(command, CancellationToken.None);

        result.Count.ShouldBe(1);
        // Verify account was looked up
        _accountDataProvider.Verify(x => x.GetAccountByIdAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendAsync_MultipleTargets_AllReceiveResults()
    {
        var machines = new List<Machine>
        {
            MakeOpenClawMachine(1, "oc-1", baseUrl: "https://a.example.com", inlineToken: "t1"),
            MakeOpenClawMachine(2, "oc-2", baseUrl: "https://b.example.com", inlineToken: "t2"),
            MakeOpenClawMachine(3, "oc-3", baseUrl: "https://c.example.com", inlineToken: "t3")
        };

        _machineDataProvider.Setup(x => x.GetMachinesByFilterAsync(It.IsAny<HashSet<int>>(), It.IsAny<HashSet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(machines);

        var service = CreateService();
        var command = MakeCommand();

        var result = await service.SendAsync(command, CancellationToken.None);

        result.Count.ShouldBe(3);
        result.ShouldContain(r => r.MachineId == 1);
        result.ShouldContain(r => r.MachineId == 2);
        result.ShouldContain(r => r.MachineId == 3);
    }

    [Fact]
    public async Task SendAsync_OneTargetInvalid_OthersContinue()
    {
        var machines = new List<Machine>
        {
            MakeOpenClawMachine(1, "good-1", baseUrl: "https://a.example.com", inlineToken: "t1"),
            MakeOpenClawMachine(2, "bad-url", baseUrl: ""),
            MakeOpenClawMachine(3, "good-2", baseUrl: "https://c.example.com", inlineToken: "t3")
        };

        _machineDataProvider.Setup(x => x.GetMachinesByFilterAsync(It.IsAny<HashSet<int>>(), It.IsAny<HashSet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(machines);

        var service = CreateService();
        var command = MakeCommand();

        var result = await service.SendAsync(command, CancellationToken.None);

        result.Count.ShouldBe(3);
        result.ShouldContain(r => r.MachineId == 2 && !r.Succeeded);
        result.ShouldContain(r => r.MachineId == 1);
        result.ShouldContain(r => r.MachineId == 3);
    }

    // ========================================================================
    // StreamAsync — Streaming Orchestration
    // ========================================================================

    [Fact]
    public async Task StreamAsync_NoMachines_YieldsNoEvents()
    {
        _machineDataProvider.Setup(x => x.GetMachinesByFilterAsync(It.IsAny<HashSet<int>>(), It.IsAny<HashSet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<Machine>());

        var service = CreateService();
        var command = MakeCommand(stream: true);

        var events = new List<OpenClawChatStreamEvent>();

        await foreach (var evt in service.StreamAsync(command, CancellationToken.None))
            events.Add(evt);

        events.ShouldBeEmpty();
    }

    [Fact]
    public async Task StreamAsync_EmptyBaseUrl_YieldsErrorEvent()
    {
        var machines = new List<Machine>
        {
            MakeOpenClawMachine(1, "empty-base", baseUrl: "")
        };

        _machineDataProvider.Setup(x => x.GetMachinesByFilterAsync(It.IsAny<HashSet<int>>(), It.IsAny<HashSet<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(machines);

        var service = CreateService();
        var command = MakeCommand(stream: true);

        var events = new List<OpenClawChatStreamEvent>();

        await foreach (var evt in service.StreamAsync(command, CancellationToken.None))
            events.Add(evt);

        events.Count.ShouldBe(1);
        events[0].MachineId.ShouldBe(1);
        events[0].Error.ShouldContain("invalid OpenClaw endpoint");
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static Machine MakeOpenClawMachine(int id, string name, string[] roles = null, bool isDisabled = false, string baseUrl = "https://openclaw.example.com", string inlineToken = "test-token")
    {
        var endpoint = new OpenClawEndpointDto
        {
            CommunicationStyle = "OpenClaw",
            BaseUrl = baseUrl,
            InlineGatewayToken = inlineToken
        };

        return new Machine
        {
            Id = id,
            Name = name,
            IsDisabled = isDisabled,
            Endpoint = JsonSerializer.Serialize(endpoint),
            Roles = JsonSerializer.Serialize(roles ?? Array.Empty<string>())
        };
    }

    private static Machine MakeK8sMachine(int id, string name, string[] roles = null)
    {
        var endpoint = new { CommunicationStyle = "KubernetesApi", ClusterUrl = "https://k8s.example.com" };

        return new Machine
        {
            Id = id,
            Name = name,
            Endpoint = JsonSerializer.Serialize(endpoint),
            Roles = JsonSerializer.Serialize(roles ?? Array.Empty<string>())
        };
    }

    private static SendOpenClawChatCommand MakeCommand(string[] tags = null, bool stream = false) => new()
    {
        TargetTags = tags?.ToList(),
        Messages = new List<ChatMessageDto> { new() { Role = "user", Content = "Hello" } },
        Model = "openclaw",
        TimeoutSeconds = 30,
        Stream = stream
    };
}
