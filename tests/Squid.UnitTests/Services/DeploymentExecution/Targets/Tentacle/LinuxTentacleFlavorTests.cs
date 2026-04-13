using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Flavors.LinuxTentacle;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

public class LinuxTentacleFlavorTests
{
    private readonly LinuxTentacleFlavor _flavor = new();

    [Fact]
    public void Id_IsLinuxTentacle()
    {
        _flavor.Id.ShouldBe("LinuxTentacle");
    }

    [Fact]
    public void CreateRuntime_ServerCommsUrlSet_ReturnsPollingMode()
    {
        var settings = new TentacleSettings
        {
            ServerCommsUrl = "https://server:10943",
            ServerUrl = "https://server:7078",
            Flavor = "LinuxTentacle"
        };

        var runtime = _flavor.CreateRuntime(BuildContext(settings));

        runtime.CommunicationMode.ShouldBe(TentacleCommunicationMode.Polling);
        runtime.Registrar.ShouldBeOfType<LinuxTentacleRegistrar>();
    }

    [Fact]
    public void CreateRuntime_ServerCommsAddressesSet_ReturnsPollingMode()
    {
        var settings = new TentacleSettings
        {
            ServerCommsAddresses = "https://server1:10943,https://server2:10943",
            ServerUrl = "https://server:7078",
            Flavor = "LinuxTentacle"
        };

        var runtime = _flavor.CreateRuntime(BuildContext(settings));

        runtime.CommunicationMode.ShouldBe(TentacleCommunicationMode.Polling);
    }

    [Fact]
    public void CreateRuntime_NoCommsUrl_ReturnsListeningMode()
    {
        var settings = new TentacleSettings
        {
            ServerUrl = "https://server:7078",
            ServerCertificate = "AABBCCDD",
            Flavor = "LinuxTentacle"
        };

        var runtime = _flavor.CreateRuntime(BuildContext(settings));

        runtime.CommunicationMode.ShouldBe(TentacleCommunicationMode.Listening);
        runtime.Registrar.ShouldBeOfType<LinuxListeningRegistrar>();
    }

    [Fact]
    public async Task CreateRuntime_ListeningMode_NoServerUrl_SkipsRegistration()
    {
        var settings = new TentacleSettings
        {
            ServerUrl = "https://localhost:7078",
            ServerCertificate = "AABBCCDD",
            Flavor = "LinuxTentacle"
        };

        var runtime = _flavor.CreateRuntime(BuildContext(settings));

        runtime.CommunicationMode.ShouldBe(TentacleCommunicationMode.Listening);

        var registration = await runtime.Registrar.RegisterAsync(
            new TentacleIdentity("sub-1", "THUMB"), CancellationToken.None);

        registration.MachineId.ShouldBe(0);
        registration.ServerThumbprint.ShouldBe("AABBCCDD");
    }

    [Fact]
    public void CreateRuntime_Metadata_ContainsExpectedKeys()
    {
        var settings = new TentacleSettings
        {
            ServerCommsUrl = "https://server:10943",
            ServerUrl = "https://server:7078",
            Flavor = "LinuxTentacle"
        };

        var runtime = _flavor.CreateRuntime(BuildContext(settings));

        runtime.Metadata.ShouldContainKey("flavor");
        runtime.Metadata["flavor"].ShouldBe("LinuxTentacle");
        runtime.Metadata.ShouldContainKey("os");
        runtime.Metadata.ShouldContainKey("communicationMode");
        runtime.Metadata["communicationMode"].ShouldBe("Polling");
        runtime.Metadata.ShouldContainKey("workspacePath");
    }

    private static TentacleFlavorContext BuildContext(TentacleSettings settings)
    {
        var configData = new Dictionary<string, string>
        {
            ["LinuxTentacle:WorkspacePath"] = "/opt/squid/work",
            ["LinuxTentacle:ListeningPort"] = "10933"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        return new TentacleFlavorContext
        {
            TentacleSettings = settings,
            Configuration = config
        };
    }
}
