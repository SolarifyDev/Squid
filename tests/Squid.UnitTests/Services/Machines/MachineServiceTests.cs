using System;
using System.Collections.Generic;
using System.Text.Json;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Machines;

public class MachineServiceTests : IDisposable
{
    private readonly Mock<IMapper> _mapper = new();
    private readonly Mock<IMachineDataProvider> _machineDataProvider = new();
    private readonly HalibutRuntime _halibutRuntime;
    private readonly MachineService _service;

    public MachineServiceTests()
    {
        var (pfxBytes, password) = TestCertHelper.GenerateSelfSignedPfx();
        var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(pfxBytes, password);
        _halibutRuntime = new HalibutRuntimeBuilder().WithServerCertificate(cert).Build();

        _mapper.Setup(m => m.Map<MachineDto>(It.IsAny<Machine>()))
            .Returns<Machine>(m => new MachineDto { Id = m.Id, Name = m.Name, MachinePolicyId = m.MachinePolicyId });

        _service = new MachineService(_mapper.Object, _machineDataProvider.Object, _halibutRuntime);
    }

    public void Dispose()
    {
        _halibutRuntime?.Dispose();
    }

    // ========================================================================
    // UpdateMachineAsync — MachinePolicyId
    // ========================================================================

    [Fact]
    public async Task UpdateMachine_ChangesMachinePolicyId()
    {
        var machine = new Machine { Id = 1, Name = "test", MachinePolicyId = 1 };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 1, MachinePolicyId = 42 };

        var response = await _service.UpdateMachineAsync(command, CancellationToken.None);

        response.Data.MachinePolicyId.ShouldBe(42);
        _machineDataProvider.Verify(p => p.UpdateMachineAsync(It.Is<Machine>(m => m.MachinePolicyId == 42), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateMachine_ChangesName()
    {
        var machine = new Machine { Id = 1, Name = "old-name" };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 1, Name = "new-name" };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        machine.Name.ShouldBe("new-name");
    }

    [Fact]
    public async Task UpdateMachine_ChangesIsDisabled()
    {
        var machine = new Machine { Id = 1, Name = "test", IsDisabled = false };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 1, IsDisabled = true };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        machine.IsDisabled.ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateMachine_ChangesRolesAndEnvironments()
    {
        var machine = new Machine { Id = 1, Name = "test", Roles = "[]", EnvironmentIds = "[]" };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand
        {
            MachineId = 1,
            Roles = new List<string> { "k8s", "production" },
            EnvironmentIds = new List<int> { 1, 2 }
        };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        machine.Roles.ShouldBe(JsonSerializer.Serialize(new[] { "k8s", "production" }));
        machine.EnvironmentIds.ShouldBe(JsonSerializer.Serialize(new[] { 1, 2 }));
    }

    [Fact]
    public async Task UpdateMachine_NullFieldsNotOverwritten()
    {
        var machine = new Machine { Id = 1, Name = "keep-this", IsDisabled = false, MachinePolicyId = 5 };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 1 };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        machine.Name.ShouldBe("keep-this");
        machine.IsDisabled.ShouldBeFalse();
        machine.MachinePolicyId.ShouldBe(5);
    }

    [Fact]
    public async Task UpdateMachine_MachineNotFound_Throws()
    {
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((Machine)null);

        var command = new UpdateMachineCommand { MachineId = 999 };

        await Should.ThrowAsync<InvalidOperationException>(() => _service.UpdateMachineAsync(command, CancellationToken.None));
    }
}

internal static class TestCertHelper
{
    public static (byte[] pfxBytes, string password) GenerateSelfSignedPfx()
    {
        const string password = "test";
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=Test", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        return (cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, password), password);
    }
}
