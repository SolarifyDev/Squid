using System.Net;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Account;
using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Machines;

public class MachineUpgradeScriptServiceTests
{
    private readonly Mock<IMachineDataProvider> _machineDataProvider = new();
    private readonly Mock<IAgentVersionProvider> _agentVersionProvider = new();
    private readonly Mock<IAccountService> _accountService = new();
    private readonly MachineScriptService _service;

    public MachineUpgradeScriptServiceTests()
    {
        _service = new MachineScriptService(
            _accountService.Object,
            _machineDataProvider.Object,
            _agentVersionProvider.Object,
            new Squid.Core.Settings.SelfCert.SelfCertSetting(),
            []);
    }

    [Fact]
    public async Task GenerateUpgradeScript_UsesEndpointMetadata_AndBuildsExpectedScript()
    {
        var machine = CreateMachine(
            machineId: 7,
            currentVersion: "1.0.3",
            releaseName: "squid-agent-7a9d48a5",
            helmNamespace: "squid-agent",
            chartRef: "oci://registry-1.docker.io/squidcd/kubernetes-agent");

        _machineDataProvider
            .Setup(x => x.GetMachinesByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(machine);
        _agentVersionProvider
            .Setup(x => x.GetLatestKubernetesAgentVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("1.0.5");

        var response = await _service.GenerateKubernetesAgentUpgradeScriptAsync(
            new GenerateKubernetesAgentUpgradeScriptCommand { MachineId = 7 },
            CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.ShouldNotBeNull();
        var data = response.Data;

        data.MachineId.ShouldBe(7);
        data.CurrentVersion.ShouldBe("1.0.3");
        data.LatestVersion.ShouldBe("1.0.5");
        data.NeedsUpgrade.ShouldBeTrue();
        data.ReleaseName.ShouldBe("squid-agent-7a9d48a5");
        data.HelmNamespace.ShouldBe("squid-agent");
        data.ChartRef.ShouldBe("oci://registry-1.docker.io/squidcd/kubernetes-agent");
        data.UpgradeScript.ShouldContain("helm upgrade --install --rollback-on-failure");
        data.UpgradeScript.ShouldContain("--version \"1.0.5\"");
        data.UpgradeScript.ShouldContain("--reuse-values");
        data.UpgradeScript.ShouldContain("--namespace squid-agent");
        data.UpgradeScript.ShouldContain("squid-agent-7a9d48a5");
        data.UpgradeScript.ShouldContain("oci://registry-1.docker.io/squidcd/kubernetes-agent");
    }

    [Fact]
    public async Task GenerateUpgradeScript_CurrentVersionUpToDate_NeedsUpgradeFalse()
    {
        var machine = CreateMachine(currentVersion: "1.0.5");

        _machineDataProvider
            .Setup(x => x.GetMachinesByIdAsync(machine.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(machine);
        _agentVersionProvider
            .Setup(x => x.GetLatestKubernetesAgentVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("1.0.5");

        var response = await _service.GenerateKubernetesAgentUpgradeScriptAsync(
            new GenerateKubernetesAgentUpgradeScriptCommand { MachineId = machine.Id },
            CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.ShouldNotBeNull();
        var data = response.Data;

        data.NeedsUpgrade.ShouldBeFalse();
    }

    [Theory]
    [InlineData("", "squid-agent", "oci://registry-1.docker.io/squidcd/kubernetes-agent", "ReleaseName")]
    [InlineData("squid-agent-abc", "", "oci://registry-1.docker.io/squidcd/kubernetes-agent", "HelmNamespace")]
    [InlineData("squid-agent-abc", "squid-agent", "", "ChartRef")]
    public async Task GenerateUpgradeScript_MetadataMissing_ReturnsConflictResponse(
        string releaseName,
        string helmNamespace,
        string chartRef,
        string missingField)
    {
        var machine = CreateMachine(
            releaseName: releaseName,
            helmNamespace: helmNamespace,
            chartRef: chartRef,
            subscriptionId: "sub-fallback-check");

        _machineDataProvider
            .Setup(x => x.GetMachinesByIdAsync(machine.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(machine);
        _agentVersionProvider
            .Setup(x => x.GetLatestKubernetesAgentVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("1.0.5");

        var response = await _service.GenerateKubernetesAgentUpgradeScriptAsync(
            new GenerateKubernetesAgentUpgradeScriptCommand { MachineId = machine.Id },
            CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.Conflict);
        response.Msg.ShouldContain(missingField);
        response.Data.ShouldBeNull();
    }

    [Fact]
    public async Task GenerateUpgradeScript_MachineNotFound_ReturnsBadRequestResponse()
    {
        _machineDataProvider
            .Setup(x => x.GetMachinesByIdAsync(404, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        var response = await _service.GenerateKubernetesAgentUpgradeScriptAsync(
            new GenerateKubernetesAgentUpgradeScriptCommand { MachineId = 404 },
            CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.BadRequest);
        response.Msg.ShouldContain("Machine 404 not found");
        response.Data.ShouldBeNull();
    }

    [Fact]
    public async Task GenerateUpgradeScript_InvalidMachineId_ReturnsBadRequestResponse()
    {
        var response = await _service.GenerateKubernetesAgentUpgradeScriptAsync(
            new GenerateKubernetesAgentUpgradeScriptCommand { MachineId = 0 },
            CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.BadRequest);
        response.Msg.ShouldContain("MachineId must be greater than 0");
        response.Data.ShouldBeNull();
    }

    [Fact]
    public async Task GenerateUpgradeScript_NonKubernetesAgentMachine_ReturnsBadRequestResponse()
    {
        var machine = new Machine
        {
            Id = 12,
            Endpoint = """{"CommunicationStyle":"KubernetesApi"}"""
        };

        _machineDataProvider
            .Setup(x => x.GetMachinesByIdAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(machine);

        var response = await _service.GenerateKubernetesAgentUpgradeScriptAsync(
            new GenerateKubernetesAgentUpgradeScriptCommand { MachineId = 12 },
            CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.BadRequest);
        response.Msg.ShouldContain("is not a KubernetesAgent target");
        response.Data.ShouldBeNull();
    }

    [Fact]
    public async Task GenerateUpgradeScript_LatestVersionMissing_ReturnsInternalServerErrorResponse()
    {
        var machine = CreateMachine();

        _machineDataProvider
            .Setup(x => x.GetMachinesByIdAsync(machine.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(machine);
        _agentVersionProvider
            .Setup(x => x.GetLatestKubernetesAgentVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string)null);

        var response = await _service.GenerateKubernetesAgentUpgradeScriptAsync(
            new GenerateKubernetesAgentUpgradeScriptCommand { MachineId = machine.Id },
            CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.InternalServerError);
        response.Msg.ShouldContain("Failed to resolve latest Kubernetes Agent version");
        response.Data.ShouldBeNull();
    }

    [Fact]
    public async Task GenerateUpgradeScript_LatestVersionInvalid_ReturnsInternalServerErrorResponse()
    {
        var machine = CreateMachine();

        _machineDataProvider
            .Setup(x => x.GetMachinesByIdAsync(machine.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(machine);
        _agentVersionProvider
            .Setup(x => x.GetLatestKubernetesAgentVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("latest");

        var response = await _service.GenerateKubernetesAgentUpgradeScriptAsync(
            new GenerateKubernetesAgentUpgradeScriptCommand { MachineId = machine.Id },
            CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.InternalServerError);
        response.Msg.ShouldContain("Latest Kubernetes Agent version 'latest' is invalid");
        response.Data.ShouldBeNull();
    }

    [Fact]
    public async Task GenerateUpgradeScript_InvalidEndpointJson_ReturnsConflictResponse()
    {
        var machine = new Machine
        {
            Id = 9,
            Endpoint = "{ invalid-json"
        };

        _machineDataProvider
            .Setup(x => x.GetMachinesByIdAsync(9, It.IsAny<CancellationToken>()))
            .ReturnsAsync(machine);

        var response = await _service.GenerateKubernetesAgentUpgradeScriptAsync(
            new GenerateKubernetesAgentUpgradeScriptCommand { MachineId = 9 },
            CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.Conflict);
        response.Msg.ShouldContain("endpoint json is invalid");
        response.Data.ShouldBeNull();
    }

    private static Machine CreateMachine(
        int machineId = 3,
        string currentVersion = "1.0.1",
        string releaseName = "squid-agent-abc",
        string helmNamespace = "squid-agent",
        string chartRef = "oci://registry-1.docker.io/squidcd/kubernetes-agent",
        string subscriptionId = "sub-abc")
    {
        var endpoint = JsonSerializer.Serialize(new KubernetesAgentEndpointDto
        {
            CommunicationStyle = "KubernetesAgent",
            SubscriptionId = subscriptionId,
            Thumbprint = "thumbprint",
            Namespace = "default",
            ReleaseName = releaseName,
            HelmNamespace = helmNamespace,
            ChartRef = chartRef,
            AgentVersion = currentVersion
        });

        return new Machine
        {
            Id = machineId,
            Endpoint = endpoint
        };
    }
}
