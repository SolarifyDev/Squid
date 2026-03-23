using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Environments;
using Squid.Core.Services.Machines;
using Squid.Core.Settings.SelfCert;
using Squid.Message.Commands.Machine;
using Squid.Message.Models.Deployments.Machine;
using DeploymentEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.UnitTests.Services.Machines;

public class MachineRegistrationServiceTests : IDisposable
{
    private readonly Mock<IMachineDataProvider> _machineDataProvider = new();
    private readonly Mock<IMachinePolicyDataProvider> _policyDataProvider = new();
    private readonly Mock<IEnvironmentDataProvider> _environmentDataProvider = new();
    private readonly HalibutRuntime _halibutRuntime;
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

        var cert = X509CertificateLoader.LoadPkcs12(pfxBytes, password);
        _halibutRuntime = new HalibutRuntimeBuilder()
            .WithServerCertificate(cert)
            .Build();

        _service = new MachineRegistrationService(
            _machineDataProvider.Object, _policyDataProvider.Object, _environmentDataProvider.Object, _halibutRuntime, _selfCertSetting);
    }

    public void Dispose()
    {
        _halibutRuntime?.Dispose();
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
            Thumbprint = "old-thumb",
            Endpoint = "{}",
            PollingSubscriptionId = "sub-123"
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
        captured.AgentVersion.ShouldBe("1.0.3");
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
            Thumbprint = "old-thumb",
            Endpoint = "{}",
            PollingSubscriptionId = "sub-123",
            AgentVersion = "1.0.0"
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
        captured.AgentVersion.ShouldBe("1.0.3");
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
            Thumbprint = "old-thumb",
            Endpoint = "{}",
            PollingSubscriptionId = "sub-123",
            AgentVersion = "1.0.0"
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
            Thumbprint = "old-thumb", Endpoint = "{}", PollingSubscriptionId = "sub-123",
            MachinePolicyId = 99
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
