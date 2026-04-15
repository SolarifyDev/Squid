using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Squid.Core.Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Environments;
using Squid.Core.Services.Machines;
using Squid.Core.Settings.SelfCert;
using Squid.Message.Commands.Machine;
using Squid.Message.Models.Deployments.Machine;
using DeploymentEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.UnitTests.Services.Machines;

public class MachineRegistrationServiceTests
{
    private readonly Mock<IMachineDataProvider> _machineDataProvider = new();
    private readonly Mock<IMachinePolicyDataProvider> _policyDataProvider = new();
    private readonly Mock<IEnvironmentDataProvider> _environmentDataProvider = new();
    private readonly Mock<IPollingTrustDistributor> _trustDistributor = new();
    private readonly SelfCertSetting _selfCertSetting;
    private readonly MachineRegistrationService _service;

    public MachineRegistrationServiceTests()
    {
        var (pfxBytes, password) = GenerateSelfSignedPfx();

        _selfCertSetting = new SelfCertSetting
        {
            Base64 = Convert.ToBase64String(pfxBytes),
            Password = password
        };

        _service = new MachineRegistrationService(
            _machineDataProvider.Object, _policyDataProvider.Object, _environmentDataProvider.Object, _trustDistributor.Object, _selfCertSetting);
    }

    [Theory]
    [InlineData("TEST,PRD", "Test,Prd")]
    [InlineData("test,prd", "Test,Prd")]
    [InlineData("Test,Prd", "Test,Prd")]
    public async Task RegisterAgent_EnvironmentNamesResolvedCaseInsensitively(string inputNames, string dbNames)
    {
        var dbEnvs = dbNames.Split(',')
            .Select((name, i) => new DeploymentEnvironment { Id = i + 1, Name = name })
            .ToList();

        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dbEnvs);

        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterKubernetesAgentAsync(CreateCommand(environments: inputNames), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.EnvironmentIds.ShouldBe("[1,2]");
    }

    [Fact]
    public async Task RegisterAgent_NoMatchingEnvironments_EnvironmentIdsEmpty()
    {
        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment>());

        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterKubernetesAgentAsync(CreateCommand(environments: "NONEXISTENT"), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.EnvironmentIds.ShouldBe("[]");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task RegisterAgent_EmptyEnvironments_EnvironmentIdsEmpty(string environments)
    {
        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterKubernetesAgentAsync(CreateCommand(environments: environments), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.EnvironmentIds.ShouldBe("[]");
        _environmentDataProvider.Verify(
            x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAgent_ReRegistration_UpdatesEnvironmentIds()
    {
        var existing = new Machine
        {
            Id = 42,
            Name = "existing-agent",
            EnvironmentIds = "99",
            Roles = "old-role",
            Endpoint = "{}"
        };

        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync("sub-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment>
            {
                new() { Id = 1, Name = "Test" },
                new() { Id = 2, Name = "Production" }
            });

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.UpdateMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        var result = await _service.RegisterKubernetesAgentAsync(
            CreateCommand(subscriptionId: "sub-123", environments: "Test,Production"), CancellationToken.None);

        result.MachineId.ShouldBe(42);
        captured.ShouldNotBeNull();
        captured.EnvironmentIds.ShouldBe("[1,2]");
    }

    [Fact]
    public async Task RegisterAgent_NewRegistration_StoresAgentVersion()
    {
        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment>());

        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterKubernetesAgentAsync(CreateCommand(agentVersion: "1.0.3"), CancellationToken.None);

        captured.ShouldNotBeNull();
        var endpoint = JsonSerializer.Deserialize<KubernetesAgentEndpointDto>(captured.Endpoint);
        endpoint.AgentVersion.ShouldBe("1.0.3");
    }

    [Fact]
    public async Task RegisterAgent_ReRegistration_UpdatesAgentVersion()
    {
        var existing = new Machine
        {
            Id = 42,
            Name = "existing-agent",
            EnvironmentIds = "[1]",
            Roles = "[\"k8s\"]",
            Endpoint = "{}"
        };

        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync("sub-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment> { new() { Id = 1, Name = "Test" } });

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.UpdateMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterKubernetesAgentAsync(
            CreateCommand(subscriptionId: "sub-123", agentVersion: "1.0.3"), CancellationToken.None);

        captured.ShouldNotBeNull();
        var endpoint = JsonSerializer.Deserialize<KubernetesAgentEndpointDto>(captured.Endpoint);
        endpoint.AgentVersion.ShouldBe("1.0.3");
    }

    [Fact]
    public async Task RegisterAgent_NewRegistration_StoresEndpointMetadata()
    {
        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment>());

        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterKubernetesAgentAsync(CreateCommand(
            releaseName: "squid-agent-12345678",
            helmNamespace: "squid-agent",
            chartRef: "oci://registry-1.docker.io/squidcd/kubernetes-agent"), CancellationToken.None);

        captured.ShouldNotBeNull();
        var endpoint = JsonSerializer.Deserialize<KubernetesAgentEndpointDto>(captured.Endpoint);
        endpoint.ShouldNotBeNull();
        endpoint.ReleaseName.ShouldBe("squid-agent-12345678");
        endpoint.HelmNamespace.ShouldBe("squid-agent");
        endpoint.ChartRef.ShouldBe("oci://registry-1.docker.io/squidcd/kubernetes-agent");
    }

    [Fact]
    public async Task RegisterAgent_ReRegistration_UpdatesEndpointMetadata()
    {
        var existing = new Machine
        {
            Id = 42,
            Name = "existing-agent",
            EnvironmentIds = "[1]",
            Roles = "[\"k8s\"]",
            Endpoint = "{}"
        };

        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync("sub-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment>());

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.UpdateMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterKubernetesAgentAsync(CreateCommand(
            subscriptionId: "sub-123",
            releaseName: "squid-agent-updated",
            helmNamespace: "squid-agent",
            chartRef: "oci://registry-1.docker.io/squidcd/kubernetes-agent"), CancellationToken.None);

        captured.ShouldNotBeNull();
        var endpoint = JsonSerializer.Deserialize<KubernetesAgentEndpointDto>(captured.Endpoint);
        endpoint.ShouldNotBeNull();
        endpoint.ReleaseName.ShouldBe("squid-agent-updated");
        endpoint.HelmNamespace.ShouldBe("squid-agent");
        endpoint.ChartRef.ShouldBe("oci://registry-1.docker.io/squidcd/kubernetes-agent");
    }

    // ========================================================================
    // Default policy auto-assignment
    // ========================================================================

    [Fact]
    public async Task RegisterAgent_NewRegistration_AssignsDefaultPolicy()
    {
        _policyDataProvider.Setup(x => x.GetDefaultAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachinePolicy { Id = 42, IsDefault = true, Name = "Default" });

        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment>());

        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterKubernetesAgentAsync(CreateCommand(), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.MachinePolicyId.ShouldBe(42);
    }

    [Fact]
    public async Task RegisterAgent_NoDefaultPolicy_MachinePolicyIdRemainsNull()
    {
        _policyDataProvider.Setup(x => x.GetDefaultAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((MachinePolicy)null);

        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment>());

        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterKubernetesAgentAsync(CreateCommand(), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.MachinePolicyId.ShouldBeNull();
    }

    [Fact]
    public async Task RegisterAgent_ReRegistration_DoesNotReassignPolicy()
    {
        var existing = new Machine
        {
            Id = 42, Name = "existing-agent", EnvironmentIds = "[1]", Roles = "[\"k8s\"]",
            Endpoint = "{}", MachinePolicyId = 99
        };

        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync("sub-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment>());

        await _service.RegisterKubernetesAgentAsync(CreateCommand(subscriptionId: "sub-123"), CancellationToken.None);

        _policyDataProvider.Verify(x => x.GetDefaultAsync(It.IsAny<CancellationToken>()), Times.Never);
        existing.MachinePolicyId.ShouldBe(99);
    }

    [Fact]
    public async Task RegisterKubernetesApi_NewRegistration_AssignsDefaultPolicy()
    {
        _policyDataProvider.Setup(x => x.GetDefaultAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachinePolicy { Id = 42, IsDefault = true, Name = "Default" });

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterKubernetesApiAsync(new RegisterKubernetesApiCommand
        {
            MachineName = "test-api",
            ClusterUrl = "https://cluster.local",
            SpaceId = 1,
        }, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.MachinePolicyId.ShouldBe(42);
    }

    // ========================================================================
    // KubernetesApi Registration — Provider Config
    // ========================================================================

    [Fact]
    public async Task RegisterKubernetesApi_WithAwsEks_EndpointJsonContainsProviderConfig()
    {
        var providerConfig = JsonSerializer.Serialize(new KubernetesApiAwsEksConfig { ClusterName = "my-eks", Region = "us-west-2" });

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterKubernetesApiAsync(new RegisterKubernetesApiCommand
        {
            MachineName = "test-eks",
            ClusterUrl = "https://eks.example.com",
            SpaceId = 1,
            ProviderType = Squid.Message.Enums.KubernetesApiEndpointProviderType.AwsEks,
            ProviderConfig = providerConfig,
        }, CancellationToken.None);

        captured.ShouldNotBeNull();
        var endpoint = JsonSerializer.Deserialize<KubernetesApiEndpointDto>(captured.Endpoint);
        endpoint.ProviderType.ShouldBe(Squid.Message.Enums.KubernetesApiEndpointProviderType.AwsEks);
        endpoint.ProviderConfig.ShouldNotBeNullOrEmpty();

        var awsConfig = JsonSerializer.Deserialize<KubernetesApiAwsEksConfig>(endpoint.ProviderConfig);
        awsConfig.ClusterName.ShouldBe("my-eks");
        awsConfig.Region.ShouldBe("us-west-2");
    }

    [Fact]
    public async Task RegisterKubernetesApi_WithoutProvider_ProviderTypeIsNone()
    {
        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterKubernetesApiAsync(new RegisterKubernetesApiCommand
        {
            MachineName = "test-basic",
            ClusterUrl = "https://cluster.local",
            SpaceId = 1,
        }, CancellationToken.None);

        captured.ShouldNotBeNull();
        var endpoint = JsonSerializer.Deserialize<KubernetesApiEndpointDto>(captured.Endpoint);
        endpoint.ProviderType.ShouldBe(Squid.Message.Enums.KubernetesApiEndpointProviderType.None);
        endpoint.ProviderConfig.ShouldBeNull();
    }

    // ========================================================================
    // Trust Reconfiguration — Agent Registration
    // ========================================================================

    [Fact]
    public async Task RegisterAgent_NewRegistration_ReconfiguresTrustAfterPersist()
    {
        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment>());

        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        var callOrder = new List<string>();
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("persist"))
            .Returns(Task.CompletedTask);
        _trustDistributor
            .Setup(t => t.Reconfigure())
            .Callback(() => callOrder.Add("reconfigure"));

        await _service.RegisterKubernetesAgentAsync(CreateCommand(), CancellationToken.None);

        callOrder.ShouldBe(new[] { "persist", "reconfigure" });
    }

    [Fact]
    public async Task RegisterAgent_ReRegistration_ReconfiguresTrustAfterPersist()
    {
        var existing = new Machine
        {
            Id = 42, Name = "existing-agent", EnvironmentIds = "[1]", Roles = "[\"k8s\"]",
            Endpoint = "{}"
        };

        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync("sub-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment>());

        var callOrder = new List<string>();
        _machineDataProvider
            .Setup(x => x.UpdateMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("persist"))
            .Returns(Task.CompletedTask);
        _trustDistributor
            .Setup(t => t.Reconfigure())
            .Callback(() => callOrder.Add("reconfigure"));

        await _service.RegisterKubernetesAgentAsync(CreateCommand(subscriptionId: "sub-123"), CancellationToken.None);

        callOrder.ShouldBe(new[] { "persist", "reconfigure" });
    }

    // ========================================================================
    // Ssh Registration
    // ========================================================================

    [Fact]
    public async Task RegisterSsh_PersistsMachineWithSshEndpointJson()
    {
        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        var result = await _service.RegisterSshAsync(new RegisterSshCommand
        {
            MachineName = "prod-ssh-01",
            SpaceId = 1,
            Host = "prod.example.com",
            Port = 2222,
            Fingerprint = "SHA256:abc123",
            RemoteWorkingDirectory = "/opt/deploy",
            Roles = new List<string> { "web", "api" },
            EnvironmentIds = new List<int> { 1, 2 },
            ResourceReferences = new List<Squid.Message.Models.Deployments.Machine.EndpointResourceReference>
            {
                new() { Type = Squid.Message.Enums.EndpointResourceType.AuthenticationAccount, ResourceId = 42 }
            }
        }, CancellationToken.None);

        result.MachineId.ShouldBe(captured.Id);
        captured.ShouldNotBeNull();
        captured.Name.ShouldBe("prod-ssh-01");
        captured.SpaceId.ShouldBe(1);
        captured.Roles.ShouldBe("[\"web\",\"api\"]");
        captured.EnvironmentIds.ShouldBe("[1,2]");

        var endpoint = JsonSerializer.Deserialize<SshEndpointDto>(captured.Endpoint);
        endpoint.CommunicationStyle.ShouldBe("Ssh");
        endpoint.Host.ShouldBe("prod.example.com");
        endpoint.Port.ShouldBe(2222);
        endpoint.Fingerprint.ShouldBe("SHA256:abc123");
        endpoint.RemoteWorkingDirectory.ShouldBe("/opt/deploy");
        endpoint.ResourceReferences.ShouldHaveSingleItem();
        endpoint.ResourceReferences[0].ResourceId.ShouldBe(42);
        endpoint.ResourceReferences[0].Type.ShouldBe(Squid.Message.Enums.EndpointResourceType.AuthenticationAccount);
    }

    [Fact]
    public async Task RegisterSsh_DefaultsPortTo22WhenZero()
    {
        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterSshAsync(new RegisterSshCommand
        {
            MachineName = "ssh-default-port",
            SpaceId = 1,
            Host = "example.com",
            Port = 0
        }, CancellationToken.None);

        var endpoint = JsonSerializer.Deserialize<SshEndpointDto>(captured.Endpoint);
        endpoint.Port.ShouldBe(22);
    }

    [Fact]
    public async Task RegisterSsh_NullRolesAndEnvironments_StoresEmptyJsonArrays()
    {
        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterSshAsync(new RegisterSshCommand
        {
            MachineName = "ssh-minimal",
            SpaceId = 1,
            Host = "host.example.com"
        }, CancellationToken.None);

        captured.Roles.ShouldBe("[]");
        captured.EnvironmentIds.ShouldBe("[]");
    }

    [Fact]
    public async Task RegisterSsh_NullMachineName_GeneratesSshPrefixedName()
    {
        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterSshAsync(new RegisterSshCommand
        {
            MachineName = null,
            SpaceId = 1,
            Host = "host.example.com"
        }, CancellationToken.None);

        captured.Name.ShouldStartWith("ssh-");
        captured.Name.Length.ShouldBe(20);
    }

    [Fact]
    public async Task RegisterSsh_AssignsDefaultPolicy()
    {
        _policyDataProvider.Setup(x => x.GetDefaultAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachinePolicy { Id = 77, IsDefault = true, Name = "Default" });

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterSshAsync(new RegisterSshCommand
        {
            MachineName = "ssh-policy-test",
            SpaceId = 1,
            Host = "host.example.com"
        }, CancellationToken.None);

        captured.MachinePolicyId.ShouldBe(77);
    }

    [Fact]
    public async Task RegisterSsh_DuplicateNameInSpace_ThrowsInvalidOperation()
    {
        _machineDataProvider
            .Setup(x => x.ExistsByNameAsync("duplicate-ssh", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _service.RegisterSshAsync(new RegisterSshCommand
            {
                MachineName = "duplicate-ssh",
                SpaceId = 1,
                Host = "host.example.com"
            }, CancellationToken.None));

        ex.Message.ShouldContain("duplicate-ssh");
        _machineDataProvider.Verify(
            x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ========================================================================
    // MachinePolicyId — explicit policy selection (generic across all types)
    // ========================================================================

    [Fact]
    public async Task RegisterAgent_ExplicitPolicyId_OverridesDefault()
    {
        _policyDataProvider.Setup(x => x.GetDefaultAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachinePolicy { Id = 1, IsDefault = true, Name = "Default" });

        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment>());

        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        var command = CreateCommand();
        command.MachinePolicyId = 99;

        await _service.RegisterKubernetesAgentAsync(command, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.MachinePolicyId.ShouldBe(99);
    }

    [Fact]
    public async Task RegisterAgent_NullPolicyId_FallsBackToDefault()
    {
        _policyDataProvider.Setup(x => x.GetDefaultAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachinePolicy { Id = 42, IsDefault = true, Name = "Default" });

        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment>());

        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        var command = CreateCommand();
        command.MachinePolicyId = null;

        await _service.RegisterKubernetesAgentAsync(command, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.MachinePolicyId.ShouldBe(42);
    }

    // ========================================================================
    // Tentacle Listening Registration
    // ========================================================================

    [Fact]
    public async Task RegisterTentacleListening_NewMachine_CreatesWithCorrectEndpoint()
    {
        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment> { new() { Id = 1, Name = "Production" } });

        _machineDataProvider
            .Setup(x => x.GetMachineByEndpointUriAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterTentacleListeningAsync(new RegisterTentacleListeningCommand
        {
            MachineName = "linux-web-01",
            SpaceId = 1,
            Roles = "web-server",
            Environments = "Production",
            Uri = "https://192.168.1.100:10933/",
            Thumbprint = "AABBCCDD",
            AgentVersion = "1.0.0"
        }, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.Name.ShouldBe("linux-web-01");
        captured.EnvironmentIds.ShouldBe("[1]");
        captured.Roles.ShouldBe("[\"web-server\"]");
        captured.Endpoint.ShouldContain("TentacleListening");
        captured.Endpoint.ShouldContain("192.168.1.100:10933");
        captured.Endpoint.ShouldContain("AABBCCDD");
    }

    [Fact]
    public async Task RegisterTentacleListening_ExistingUri_UpdatesMachine()
    {
        var existing = new Machine
        {
            Id = 10, Name = "linux-web-01", EnvironmentIds = "[1]", Roles = "[\"old\"]",
            Endpoint = "{}"
        };

        _machineDataProvider
            .Setup(x => x.GetMachineByEndpointUriAsync("https://192.168.1.100:10933/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment> { new() { Id = 2, Name = "Staging" } });

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.UpdateMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        var result = await _service.RegisterTentacleListeningAsync(new RegisterTentacleListeningCommand
        {
            MachineName = "linux-web-01",
            SpaceId = 1,
            Roles = "web-server",
            Environments = "Staging",
            Uri = "https://192.168.1.100:10933/",
            Thumbprint = "NEWTHUMB"
        }, CancellationToken.None);

        result.MachineId.ShouldBe(10);
        captured.ShouldNotBeNull();
        captured.Roles.ShouldBe("[\"web-server\"]");
        captured.EnvironmentIds.ShouldBe("[2]");
        captured.Endpoint.ShouldContain("NEWTHUMB");
    }

    [Fact]
    public async Task RegisterTentacleListening_ExplicitPolicyId_UsesIt()
    {
        _policyDataProvider.Setup(x => x.GetDefaultAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachinePolicy { Id = 1, IsDefault = true, Name = "Default" });

        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment>());

        _machineDataProvider
            .Setup(x => x.GetMachineByEndpointUriAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterTentacleListeningAsync(new RegisterTentacleListeningCommand
        {
            MachineName = "linux-policy-test",
            SpaceId = 1,
            Uri = "https://10.0.0.5:10933/",
            Thumbprint = "AABB",
            MachinePolicyId = 55
        }, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.MachinePolicyId.ShouldBe(55);
    }

    // ========================================================================
    // Tentacle Polling Registration
    // ========================================================================

    [Fact]
    public async Task RegisterTentaclePolling_NewMachine_CreatesWithCorrectEndpoint()
    {
        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment> { new() { Id = 3, Name = "Dev" } });

        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterTentaclePollingAsync(new RegisterTentaclePollingCommand
        {
            MachineName = "linux-poll-01",
            SpaceId = 1,
            Roles = "web-server",
            Environments = "Dev",
            Thumbprint = "EEFF0011",
            SubscriptionId = "poll-sub-001",
            AgentVersion = "1.0.0"
        }, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.Name.ShouldBe("linux-poll-01");
        captured.EnvironmentIds.ShouldBe("[3]");
        captured.Roles.ShouldBe("[\"web-server\"]");
        captured.Endpoint.ShouldContain("TentaclePolling");
        captured.Endpoint.ShouldContain("poll-sub-001");
        captured.Endpoint.ShouldContain("EEFF0011");
    }

    [Fact]
    public async Task RegisterTentaclePolling_ExplicitPolicyId_UsesIt()
    {
        _policyDataProvider.Setup(x => x.GetDefaultAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachinePolicy { Id = 1, IsDefault = true, Name = "Default" });

        _environmentDataProvider
            .Setup(x => x.GetEnvironmentsByNamesAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeploymentEnvironment>());

        _machineDataProvider
            .Setup(x => x.GetMachineBySubscriptionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Machine)null);

        Machine captured = null;
        _machineDataProvider
            .Setup(x => x.AddMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<Machine, bool, CancellationToken>((m, _, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.RegisterTentaclePollingAsync(new RegisterTentaclePollingCommand
        {
            MachineName = "linux-poll-policy",
            SpaceId = 1,
            Thumbprint = "AABB",
            SubscriptionId = "poll-sub-002",
            MachinePolicyId = 77
        }, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.MachinePolicyId.ShouldBe(77);
    }

    private static RegisterKubernetesAgentCommand CreateCommand(
        string machineName = "test-agent",
        string thumbprint = "AABBCCDD",
        string subscriptionId = "sub-test-001",
        string roles = "k8s",
        string environments = "Test,Production",
        int spaceId = 1,
        string agentVersion = null,
        string releaseName = null,
        string helmNamespace = null,
        string chartRef = null)
    {
        return new RegisterKubernetesAgentCommand
        {
            MachineName = machineName,
            Thumbprint = thumbprint,
            SubscriptionId = subscriptionId,
            Roles = roles,
            Environments = environments,
            SpaceId = spaceId,
            Namespace = "default",
            AgentVersion = agentVersion,
            ReleaseName = releaseName,
            HelmNamespace = helmNamespace,
            ChartRef = chartRef
        };
    }

    private static (byte[] pfxBytes, string password) GenerateSelfSignedPfx()
    {
        const string password = "test";

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));

        return (cert.Export(X509ContentType.Pfx, password), password);
    }
}
