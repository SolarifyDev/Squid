using System.Linq;
using System.Net;
using Squid.Core.Services.Account;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Machines.Scripts.Tentacle;
using Squid.Message.Commands.Account;
using Squid.Message.Commands.Machine;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.Machines.Scripts.Tentacle;

public class GenerateTentacleInstallScriptServiceTests
{
    private readonly Mock<IAccountService> _accountService = new();
    private readonly Mock<IMachineDataProvider> _machineDataProvider = new();
    private readonly Mock<IAgentVersionProvider> _agentVersionProvider = new();
    private readonly Mock<ITentacleCommsUrlProbe> _commsUrlProbe = new();

    public GenerateTentacleInstallScriptServiceTests()
    {
        _accountService
            .Setup(x => x.CreateApiKeyAsync(CurrentUsers.InternalUser.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateApiKeyResponseData { ApiKey = "API-TEST" });

        _commsUrlProbe
            .Setup(x => x.ProbeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TentacleCommsProbeResult { Skipped = true, Detail = "Stubbed for unit test" });
    }

    [Fact]
    public async Task GenerateTentacleInstallScript_NoOsFilter_ReturnsAllBuilderOutputs()
    {
        var service = BuildService(
            new FakeBuilder("linux-docker-run", "Linux"),
            new FakeBuilder("linux-binary", "Linux"),
            new FakeBuilder("windows-msi", "Windows"));

        var response = await service.GenerateTentacleInstallScriptAsync(
            new GenerateTentacleInstallScriptCommand { CommunicationMode = "Polling" },
            CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.Scripts.Select(s => s.Id)
            .ShouldBe(["linux-docker-run", "linux-binary", "windows-msi"]);
    }

    [Fact]
    public async Task GenerateTentacleInstallScript_OsFilterLinux_ExcludesWindowsBuilders()
    {
        var service = BuildService(
            new FakeBuilder("linux-docker-run", "Linux"),
            new FakeBuilder("windows-msi", "Windows"),
            new FakeBuilder("windows-choco", "Windows"));

        var response = await service.GenerateTentacleInstallScriptAsync(
            new GenerateTentacleInstallScriptCommand
            {
                CommunicationMode = "Polling",
                OperatingSystem = "Linux"
            },
            CancellationToken.None);

        response.Data.Scripts.Count.ShouldBe(1);
        response.Data.Scripts[0].Id.ShouldBe("linux-docker-run");
    }

    [Fact]
    public async Task GenerateTentacleInstallScript_OsFilterIsCaseInsensitive()
    {
        var service = BuildService(new FakeBuilder("linux-docker-run", "Linux"));

        var response = await service.GenerateTentacleInstallScriptAsync(
            new GenerateTentacleInstallScriptCommand
            {
                CommunicationMode = "Polling",
                OperatingSystem = "linux"
            },
            CancellationToken.None);

        response.Data.Scripts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GenerateTentacleInstallScript_NullCommand_ReturnsBadRequest()
    {
        var service = BuildService();

        var response = await service.GenerateTentacleInstallScriptAsync(null, CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GenerateTentacleInstallScript_ApiKeyCreationFails_ReturnsInternalError()
    {
        _accountService
            .Setup(x => x.CreateApiKeyAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateApiKeyResponseData { ApiKey = null });

        var service = BuildService(new FakeBuilder("linux-docker-run", "Linux"));

        var response = await service.GenerateTentacleInstallScriptAsync(
            new GenerateTentacleInstallScriptCommand { CommunicationMode = "Polling" },
            CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.InternalServerError);
    }

    private MachineScriptService BuildService(params ITentacleInstallScriptBuilder[] builders)
    {
        return new MachineScriptService(
            _accountService.Object,
            _machineDataProvider.Object,
            _agentVersionProvider.Object,
            new Squid.Core.Settings.SelfCert.SelfCertSetting(),
            builders,
            _commsUrlProbe.Object);
    }

    private sealed class FakeBuilder : ITentacleInstallScriptBuilder
    {
        public FakeBuilder(string id, string os)
        {
            Id = id;
            OperatingSystem = os;
        }

        public string Id { get; }
        public string Label => Id;
        public string OperatingSystem { get; }
        public string InstallationMethod => "Fake";
        public string ScriptType => "bash";
        public bool IsRecommended => false;

        public TentacleInstallScript Build(TentacleInstallContext context) => new()
        {
            Id = Id,
            Label = Label,
            OperatingSystem = OperatingSystem,
            InstallationMethod = InstallationMethod,
            ScriptType = ScriptType,
            IsRecommended = IsRecommended,
            Content = $"fake-content-for-{Id}"
        };
    }
}
