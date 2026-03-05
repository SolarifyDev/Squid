using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Environments;
using Squid.Core.Services.Machines;
using Squid.Core.Settings.SelfCert;
using Squid.Message.Commands.Machine;
using DeploymentEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.UnitTests.Services.Machines;

public class MachineRegistrationServiceTests : IDisposable
{
    private readonly Mock<IMachineDataProvider> _machineDataProvider = new();
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
            _machineDataProvider.Object, _environmentDataProvider.Object, _halibutRuntime, _selfCertSetting);
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

    private static RegisterKubernetesAgentCommand CreateCommand(
        string machineName = "test-agent",
        string thumbprint = "AABBCCDD",
        string subscriptionId = "sub-test-001",
        string roles = "k8s",
        string environments = "Test,Production",
        int spaceId = 1)
    {
        return new RegisterKubernetesAgentCommand
        {
            MachineName = machineName,
            Thumbprint = thumbprint,
            SubscriptionId = subscriptionId,
            Roles = roles,
            Environments = environments,
            SpaceId = spaceId,
            Namespace = "default"
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
