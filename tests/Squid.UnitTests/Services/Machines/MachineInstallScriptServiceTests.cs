using System;
using Squid.Core.Services.Account;
using Squid.Core.Services.Authentication;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;
using Squid.Message.Models.Account;

namespace Squid.UnitTests.Services.Machines;

public class MachineInstallScriptServiceTests
{
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IUserTokenService> _userTokenService = new();
    private readonly Mock<IAccountService> _accountService = new();
    private readonly MachineInstallScriptService _service;

    public MachineInstallScriptServiceTests()
    {
        _currentUser.Setup(x => x.Id).Returns(1);

        _accountService
            .Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserAccountDto { Id = 1, UserName = "admin", DisplayName = "Admin" });

        _userTokenService
            .Setup(x => x.GenerateToken(It.IsAny<Squid.Core.Persistence.Entities.Account.UserAccount>()))
            .Returns(("test-bearer-token", DateTime.UtcNow.AddHours(1)));

        _service = new MachineInstallScriptService(
            _currentUser.Object,
            _userTokenService.Object,
            _accountService.Object);
    }

    private static GenerateKubernetesAgentInstallScriptCommand CreateCommand(
        string agentName = "my-agent",
        string serverUrl = "https://squid.example.com",
        string serverCommsUrl = "https://squid.example.com:10943")
    {
        return new GenerateKubernetesAgentInstallScriptCommand
        {
            AgentName = agentName,
            ServerUrl = serverUrl,
            ServerCommsUrl = serverCommsUrl,
            EnvironmentIds = [1, 2],
            Tags = ["k8s", "web"],
            SpaceId = 1
        };
    }

    [Fact]
    public async Task GenerateScript_ReturnsSubscriptionId_As32CharGuid()
    {
        var result = await _service.GenerateKubernetesAgentScriptAsync(CreateCommand(), CancellationToken.None);

        result.SubscriptionId.ShouldNotBeNullOrWhiteSpace();
        result.SubscriptionId.Length.ShouldBe(32);
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsHelmUpgradeInstall()
    {
        var result = await _service.GenerateKubernetesAgentScriptAsync(CreateCommand(), CancellationToken.None);

        result.AgentInstallScript.ShouldStartWith("helm upgrade --install --rollback-on-failure");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ReleaseNameIsSafe()
    {
        var result = await _service.GenerateKubernetesAgentScriptAsync(CreateCommand(), CancellationToken.None);

        result.AgentInstallScript.ShouldContain($"squid-agent-{result.SubscriptionId[..8]}");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsMachineName()
    {
        var result = await _service.GenerateKubernetesAgentScriptAsync(CreateCommand(), CancellationToken.None);

        result.AgentInstallScript.ShouldContain("tentacle.machineName=\"my-agent\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsNamespaceAndVersion()
    {
        var result = await _service.GenerateKubernetesAgentScriptAsync(CreateCommand(), CancellationToken.None);

        result.AgentInstallScript.ShouldContain("--create-namespace --namespace squid-agent");
        result.AgentInstallScript.ShouldContain("--version \"0.*.*\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsOciRegistry()
    {
        var result = await _service.GenerateKubernetesAgentScriptAsync(CreateCommand(), CancellationToken.None);

        result.AgentInstallScript.ShouldContain("oci://registry-1.docker.io/squidcd/kubernetes-agent");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsServerUrls()
    {
        var result = await _service.GenerateKubernetesAgentScriptAsync(CreateCommand(), CancellationToken.None);

        result.AgentInstallScript.ShouldContain("tentacle.serverUrl=\"https://squid.example.com\"");
        result.AgentInstallScript.ShouldContain("tentacle.serverCommsUrl=\"https://squid.example.com:10943\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsBearerToken()
    {
        var result = await _service.GenerateKubernetesAgentScriptAsync(CreateCommand(), CancellationToken.None);

        result.AgentInstallScript.ShouldContain("tentacle.bearerToken=\"test-bearer-token\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsRolesAndEnvironments()
    {
        var result = await _service.GenerateKubernetesAgentScriptAsync(CreateCommand(), CancellationToken.None);

        result.AgentInstallScript.ShouldContain("tentacle.roles=\"k8s,web\"");
        result.AgentInstallScript.ShouldContain("tentacle.environmentIds=\"1,2\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsSubscriptionId()
    {
        var result = await _service.GenerateKubernetesAgentScriptAsync(CreateCommand(), CancellationToken.None);

        result.AgentInstallScript.ShouldContain($"tentacle.subscriptionId=\"{result.SubscriptionId}\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsFlavorAndSpaceId()
    {
        var result = await _service.GenerateKubernetesAgentScriptAsync(CreateCommand(), CancellationToken.None);

        result.AgentInstallScript.ShouldContain("tentacle.flavor=\"KubernetesAgent\"");
        result.AgentInstallScript.ShouldContain("tentacle.spaceId=\"1\"");
    }

    [Fact]
    public async Task GenerateScript_EmptyAgentName_FallsBackToReleaseName()
    {
        var result = await _service.GenerateKubernetesAgentScriptAsync(
            CreateCommand(agentName: ""), CancellationToken.None);

        var releaseName = $"squid-agent-{result.SubscriptionId[..8]}";
        result.AgentInstallScript.ShouldContain($"tentacle.machineName=\"{releaseName}\"");
    }

    [Fact]
    public async Task GenerateScript_NfsCsiDriverScript_ContainsHelmCommands()
    {
        var result = await _service.GenerateKubernetesAgentScriptAsync(CreateCommand(), CancellationToken.None);

        result.NfsCsiDriverScript.ShouldContain("helm upgrade --install --rollback-on-failure");
        result.NfsCsiDriverScript.ShouldContain("csi-driver-nfs");
    }

    [Fact]
    public async Task GenerateScript_UsesBackslashLineContinuation()
    {
        var result = await _service.GenerateKubernetesAgentScriptAsync(CreateCommand(), CancellationToken.None);

        result.AgentInstallScript.ShouldContain(" \\\n");
        result.NfsCsiDriverScript.ShouldContain(" \\\n");
    }

    [Fact]
    public async Task GenerateScript_NullUserId_ThrowsInvalidOperation()
    {
        _currentUser.Setup(x => x.Id).Returns((int?)null);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.GenerateKubernetesAgentScriptAsync(CreateCommand(), CancellationToken.None));
    }

    [Fact]
    public async Task GenerateScript_UserNotFound_ThrowsInvalidOperation()
    {
        _accountService
            .Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserAccountDto)null);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.GenerateKubernetesAgentScriptAsync(CreateCommand(), CancellationToken.None));
    }
}
