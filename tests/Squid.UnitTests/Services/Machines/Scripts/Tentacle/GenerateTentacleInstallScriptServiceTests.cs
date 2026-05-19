using System.Linq;
using System.Net;
using Squid.Core.Services.Account;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Machines.Scripts.Tentacle;
using Squid.Core.Services.Machines.Upgrade;
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
    private readonly Mock<ITentacleVersionRegistry> _versionRegistry = new();

    public GenerateTentacleInstallScriptServiceTests()
    {
        _accountService
            .Setup(x => x.CreateApiKeyAsync(CurrentUsers.InternalUser.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateApiKeyResponseData { ApiKey = "API-TEST" });

        _commsUrlProbe
            .Setup(x => x.ProbeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TentacleCommsProbeResult { Skipped = true, Detail = "Stubbed for unit test" });

        _versionRegistry
            .Setup(x => x.GetLatestVersionAsync(It.IsAny<string>(), It.IsAny<MachineRuntimeCapabilities>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("1.6.0");
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
    public async Task GenerateTentacleInstallScript_AlwaysIncludesDownloadsList_FilteredByOsFilter()
    {
        // The Downloads array bundles the same payload as
        // GET /api/machines/tentacle-downloads so the FE wizard can render
        // both UX paths (paste-script + manual-download) from a single API call.
        var service = BuildService(
            new LinuxBinaryScriptBuilder(),
            new WindowsPowerShellScriptBuilder());

        var noFilter = await service.GenerateTentacleInstallScriptAsync(
            new GenerateTentacleInstallScriptCommand { CommunicationMode = "Listening", ServerUrl = "https://squid:7078" },
            CancellationToken.None);

        var winOnly = await service.GenerateTentacleInstallScriptAsync(
            new GenerateTentacleInstallScriptCommand { CommunicationMode = "Listening", ServerUrl = "https://squid:7078", OperatingSystem = "Windows" },
            CancellationToken.None);

        var linuxOnly = await service.GenerateTentacleInstallScriptAsync(
            new GenerateTentacleInstallScriptCommand { CommunicationMode = "Listening", ServerUrl = "https://squid:7078", OperatingSystem = "Linux" },
            CancellationToken.None);

        noFilter.Data.Downloads.Count.ShouldBe(6, customMessage:
            "no OS filter → 2 Windows + 4 Linux downloads, mirroring tentacle-downloads endpoint");
        winOnly.Data.Downloads.Count.ShouldBe(2);
        winOnly.Data.Downloads.ShouldAllBe(d => d.OperatingSystem == "Windows");
        linuxOnly.Data.Downloads.Count.ShouldBe(4);
        linuxOnly.Data.Downloads.ShouldAllBe(d => d.OperatingSystem == "Linux");
    }

    [Fact]
    public async Task GenerateTentacleInstallScript_DownloadsAndScripts_HonourSameOsFilter()
    {
        // Cross-field consistency pin: Scripts[] and Downloads[] both honour
        // the same OperatingSystem filter — Linux scripts paired with Linux
        // downloads, Windows with Windows. A divergence here would confuse the
        // FE wizard (operator picks Windows, sees Linux download links).
        var service = BuildService(
            new LinuxBinaryScriptBuilder(),
            new WindowsPowerShellScriptBuilder());

        var winResp = await service.GenerateTentacleInstallScriptAsync(
            new GenerateTentacleInstallScriptCommand { CommunicationMode = "Listening", ServerUrl = "https://squid:7078", OperatingSystem = "Windows" },
            CancellationToken.None);

        winResp.Data.Scripts.ShouldAllBe(s => s.OperatingSystem == "Windows");
        winResp.Data.Downloads.ShouldAllBe(d => d.OperatingSystem == "Windows");
    }

    [Fact]
    public async Task GenerateTentacleInstallScript_OsFilterWindows_RealBuilder_ReturnsPowerShellScript()
    {
        // End-to-end test using the REAL WindowsPowerShellScriptBuilder (not a fake)
        // — confirms (a) the OS filter routes through to the real builder, (b) the
        // real builder emits PowerShell content not a stub. Catches a future
        // mis-registration where the Windows builder gets dropped from DI scan
        // and the service silently returns zero scripts under OS=Windows filter.
        var service = BuildService(
            new LinuxBinaryScriptBuilder(),
            new WindowsPowerShellScriptBuilder());

        var response = await service.GenerateTentacleInstallScriptAsync(
            new GenerateTentacleInstallScriptCommand
            {
                CommunicationMode = "Listening",
                OperatingSystem = "Windows",
                ServerUrl = "https://squid:7078",
                ListeningHostName = "win-host",
                ListeningPort = 10933
            },
            CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.Scripts.Count.ShouldBe(1, customMessage:
            "OS=Windows filter must return exactly the Windows builders — Linux must be filtered out.");
        response.Data.Scripts[0].Id.ShouldBe("windows-powershell");
        response.Data.Scripts[0].ScriptType.ShouldBe("powershell");
        response.Data.Scripts[0].Content.ShouldContain("Invoke-WebRequest", customMessage:
            "Real Windows builder must emit PowerShell-flavoured download command.");
        response.Data.Scripts[0].Content.ShouldContain("$tentacle register", customMessage:
            "Real Windows builder must invoke the discovery-resolved $tentacle variable for register " +
            "(path comes from install-info.json — no hardcoded C:\\Program Files\\...).");
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
            _commsUrlProbe.Object,
            _versionRegistry.Object);
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
