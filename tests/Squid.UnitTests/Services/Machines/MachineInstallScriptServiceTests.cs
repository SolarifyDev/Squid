using System;
using System.Net;
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
    private readonly Mock<IMachineDataProvider> _machineDataProvider = new();
    private readonly Mock<IAgentVersionProvider> _agentVersionProvider = new();
    private readonly MachineScriptService _service;

    public MachineInstallScriptServiceTests()
    {
        _currentUser.Setup(x => x.Id).Returns(1);

        _accountService
            .Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserAccountDto { Id = 1, UserName = "admin", DisplayName = "Admin" });

        _userTokenService
            .Setup(x => x.GenerateToken(It.IsAny<Squid.Core.Persistence.Entities.Account.UserAccount>()))
            .Returns(("test-bearer-token", DateTime.UtcNow.AddHours(1)));

        _service = new MachineScriptService(
            _currentUser.Object,
            _userTokenService.Object,
            _accountService.Object,
            _machineDataProvider.Object,
            _agentVersionProvider.Object);
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
            Environments = ["Test", "Production"],
            Tags = ["k8s", "web"],
            SpaceId = 1
        };
    }

    private async Task<GenerateKubernetesAgentInstallScriptData> GenerateSuccessDataAsync(
        GenerateKubernetesAgentInstallScriptCommand command = null)
    {
        var response = await _service.GenerateKubernetesAgentInstallScriptAsync(
            command ?? CreateCommand(),
            CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.ShouldNotBeNull();
        return response.Data;
    }

    [Fact]
    public async Task GenerateScript_ReturnsSubscriptionId_As32CharGuid()
    {
        var result = await GenerateSuccessDataAsync();

        result.SubscriptionId.ShouldNotBeNullOrWhiteSpace();
        result.SubscriptionId.Length.ShouldBe(32);
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsHelmUpgradeInstall()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldStartWith("helm upgrade --install --rollback-on-failure");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ReleaseNameIsSafe()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain($"squid-agent-{result.SubscriptionId[..8]}");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsMachineName()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain("tentacle.machineName=\"my-agent\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsNamespace()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain("--create-namespace --namespace squid-agent");
        result.AgentInstallScript.ShouldContain("--version \"1.*.*\"");
    }

    [Theory]
    [InlineData("production", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public async Task GenerateScript_AgentInstallScript_DefaultNamespaceOnlyWhenProvided(string defaultNamespace, bool shouldContain)
    {
        var command = CreateCommand();
        command.DefaultNamespace = defaultNamespace;

        var result = await GenerateSuccessDataAsync(command);

        if (shouldContain)
            result.AgentInstallScript.ShouldContain("kubernetes.namespace=\"production\"");
        else
            result.AgentInstallScript.ShouldNotContain("kubernetes.namespace");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsOciRegistry()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain("tentacle.chartRef=\"oci://registry-1.docker.io/squidcd/kubernetes-agent\"");
        result.AgentInstallScript.ShouldContain("oci://registry-1.docker.io/squidcd/kubernetes-agent");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_UsesCustomChartRef_WhenProvided()
    {
        const string customChartRef = "oci://registry.example.com/squid/kubernetes-agent";
        var command = CreateCommand();
        command.ChartRef = customChartRef;

        var result = await GenerateSuccessDataAsync(command);

        result.AgentInstallScript.ShouldContain($"tentacle.chartRef=\"{customChartRef}\"");
        result.AgentInstallScript.ShouldContain(customChartRef);
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsServerUrls()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain("tentacle.serverUrl=\"https://squid.example.com\"");
        result.AgentInstallScript.ShouldContain("tentacle.serverCommsUrl=\"https://squid.example.com:10943\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsBearerToken()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain("tentacle.bearerToken=\"test-bearer-token\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsRolesAndEnvironmentsAsHelmArrays()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain("tentacle.roles=\"{k8s,web}\"");
        result.AgentInstallScript.ShouldContain("tentacle.environments=\"{Test,Production}\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_SingleItemArrays()
    {
        var command = CreateCommand();
        command.Tags = ["web"];
        command.Environments = ["Test"];

        var result = await GenerateSuccessDataAsync(command);

        result.AgentInstallScript.ShouldContain("tentacle.roles=\"{web}\"");
        result.AgentInstallScript.ShouldContain("tentacle.environments=\"{Test}\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_EmptyArrays()
    {
        var command = CreateCommand();
        command.Tags = [];
        command.Environments = [];

        var result = await GenerateSuccessDataAsync(command);

        result.AgentInstallScript.ShouldContain("tentacle.roles=\"{}\"");
        result.AgentInstallScript.ShouldContain("tentacle.environments=\"{}\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsSubscriptionId()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain($"tentacle.subscriptionId=\"{result.SubscriptionId}\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsFlavorAndSpaceId()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain("tentacle.flavor=\"KubernetesAgent\"");
        result.AgentInstallScript.ShouldContain("tentacle.spaceId=\"1\"");
    }

    [Fact]
    public async Task GenerateScript_EmptyAgentName_FallsBackToReleaseName()
    {
        var result = await GenerateSuccessDataAsync(CreateCommand(agentName: ""));

        var releaseName = $"squid-agent-{result.SubscriptionId[..8]}";
        result.AgentInstallScript.ShouldContain($"tentacle.machineName=\"{releaseName}\"");
    }

    [Fact]
    public async Task GenerateScript_NfsCsiDriverScript_ContainsHelmCommands()
    {
        var result = await GenerateSuccessDataAsync();

        result.NfsCsiDriverScript.ShouldContain("helm upgrade --install --rollback-on-failure");
        result.NfsCsiDriverScript.ShouldContain("csi-driver-nfs");
    }

    [Fact]
    public async Task GenerateScript_UsesBackslashLineContinuation()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain(" \\\n");
        result.NfsCsiDriverScript.ShouldContain(" \\\n");
    }

    [Fact]
    public async Task GenerateScript_NullUserId_ReturnsUnauthorizedResponse()
    {
        _currentUser.Setup(x => x.Id).Returns((int?)null);

        var response = await _service.GenerateKubernetesAgentInstallScriptAsync(CreateCommand(), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.Unauthorized);
        response.Msg.ShouldContain("Cannot resolve current user");
        response.Data.ShouldBeNull();
    }

    [Fact]
    public async Task GenerateScript_UserNotFound_ReturnsUnauthorizedResponse()
    {
        _accountService
            .Setup(x => x.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserAccountDto)null);

        var response = await _service.GenerateKubernetesAgentInstallScriptAsync(CreateCommand(), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.Unauthorized);
        response.Msg.ShouldContain("User account 1 not found");
        response.Data.ShouldBeNull();
    }
}
