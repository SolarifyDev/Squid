using System.Text.Json;
using Squid.Core.Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Machines;
using Squid.Message.Commands.Machine;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Machines;

public class MachineServiceTests
{
    private readonly Mock<IMapper> _mapper = new();
    private readonly Mock<IMachineDataProvider> _machineDataProvider = new();
    private readonly Mock<IPollingTrustDistributor> _trustDistributor = new();
    private readonly MachineService _service;

    public MachineServiceTests()
    {
        _mapper.Setup(m => m.Map<MachineDto>(It.IsAny<Machine>()))
            .Returns<Machine>(m => new MachineDto { Id = m.Id, Name = m.Name, MachinePolicyId = m.MachinePolicyId });

        _service = new MachineService(_mapper.Object, _machineDataProvider.Object, _trustDistributor.Object);
    }

    // ========================================================================
    // UpdateMachineAsync — ApplyUpdate
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

    [Fact]
    public async Task UpdateMachine_NullThumbprint_DoesNotChangeExisting()
    {
        var machine = new Machine { Id = 1, Name = "agent-1", Thumbprint = "EXISTING-THUMB", PollingSubscriptionId = "poll://sub-1/" };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 1, Name = "renamed" };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        machine.Thumbprint.ShouldBe("EXISTING-THUMB");
    }

    // ========================================================================
    // UpdateMachineAsync — Trust Reconfiguration
    // ========================================================================

    [Fact]
    public async Task UpdateMachine_PersistsThenReconfiguresTrust()
    {
        var machine = new Machine { Id = 1, Name = "agent-1", Thumbprint = "OLD-THUMB", PollingSubscriptionId = "poll://sub-1/" };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var callOrder = new List<string>();
        _machineDataProvider
            .Setup(p => p.UpdateMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("persist"))
            .Returns(Task.CompletedTask);
        _trustDistributor
            .Setup(t => t.Reconfigure())
            .Callback(() => callOrder.Add("reconfigure"));

        var command = new UpdateMachineCommand { MachineId = 1, Thumbprint = "NEW-THUMB" };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        callOrder.ShouldBe(new[] { "persist", "reconfigure" });
    }

    [Fact]
    public async Task UpdateMachine_NonPollingMachine_StillReconfigures()
    {
        var machine = new Machine { Id = 1, Name = "api-target", Thumbprint = "OLD-THUMB", PollingSubscriptionId = null };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 1, Thumbprint = "NEW-THUMB" };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        _trustDistributor.Verify(t => t.Reconfigure(), Times.Once);
    }

    [Fact]
    public async Task UpdateMachine_DisablingMachine_ReconfiguresTrust()
    {
        var machine = new Machine { Id = 1, Name = "agent-1", Thumbprint = "THUMB", PollingSubscriptionId = "poll://sub-1/", IsDisabled = false };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        var command = new UpdateMachineCommand { MachineId = 1, IsDisabled = true };

        await _service.UpdateMachineAsync(command, CancellationToken.None);

        _trustDistributor.Verify(t => t.Reconfigure(), Times.Once);
    }

    // ========================================================================
    // DeleteMachinesAsync — Trust Reconfiguration
    // ========================================================================

    [Fact]
    public async Task DeleteMachines_PersistsThenReconfiguresTrust()
    {
        var machines = new List<Machine> { new() { Id = 1, Name = "agent-1", Thumbprint = "THUMB-A" } };
        _machineDataProvider.Setup(p => p.GetMachinesByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(machines);

        var callOrder = new List<string>();
        _machineDataProvider
            .Setup(p => p.DeleteMachinesAsync(It.IsAny<List<Machine>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("persist"))
            .Returns(Task.CompletedTask);
        _trustDistributor
            .Setup(t => t.Reconfigure())
            .Callback(() => callOrder.Add("reconfigure"));

        var command = new DeleteMachinesCommand { Ids = new List<int> { 1 } };

        await _service.DeleteMachinesAsync(command, CancellationToken.None);

        callOrder.ShouldBe(new[] { "persist", "reconfigure" });
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
