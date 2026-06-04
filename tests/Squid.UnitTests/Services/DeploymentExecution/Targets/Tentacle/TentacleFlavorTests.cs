using Microsoft.Extensions.Configuration;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Flavors.Tentacle;

namespace Squid.UnitTests.Services.DeploymentExecution.Targets.Tentacle;

public class TentacleFlavorTests
{
    private readonly TentacleFlavor _flavor = new();

    [Fact]
    public void Id_IsTentacle_AndKeepsLinuxTentacleAlias()
    {
        // Renamed from "LinuxTentacle" → OS-neutral "Tentacle" (the flavor is cross-platform).
        // "LinuxTentacle" stays as an alias so already-deployed agents / old snippets / configs
        // that pass the old --flavor value still resolve (backward compat).
        _flavor.Id.ShouldBe("Tentacle");
        _flavor.Aliases.ShouldContain("LinuxTentacle");
    }

    [Fact]
    public void CreateRuntime_ServerCommsUrlSet_ReturnsPollingMode()
    {
        var settings = new TentacleSettings
        {
            ServerCommsUrl = "https://server:10943",
            ServerUrl = "https://server:7078",
            Flavor = "Tentacle"
        };

        var runtime = _flavor.CreateRuntime(BuildContext(settings));

        runtime.CommunicationMode.ShouldBe(TentacleCommunicationMode.Polling);
        runtime.Registrar.ShouldBeOfType<TentaclePollingRegistrar>();
    }

    [Fact]
    public void CreateRuntime_ServerCommsAddressesSet_ReturnsPollingMode()
    {
        var settings = new TentacleSettings
        {
            ServerCommsAddresses = "https://server1:10943,https://server2:10943",
            ServerUrl = "https://server:7078",
            Flavor = "Tentacle"
        };

        var runtime = _flavor.CreateRuntime(BuildContext(settings));

        runtime.CommunicationMode.ShouldBe(TentacleCommunicationMode.Polling);
    }

    [Fact]
    public void CreateRuntime_NoCommsUrl_ReturnsListeningMode_WithCredentials()
    {
        var settings = new TentacleSettings
        {
            ServerUrl = "https://server:7078",
            ApiKey = "API-TEST",
            Flavor = "Tentacle"
        };

        var runtime = _flavor.CreateRuntime(BuildContext(settings));

        runtime.CommunicationMode.ShouldBe(TentacleCommunicationMode.Listening);
        runtime.Registrar.ShouldBeOfType<TentacleListeningRegistrar>();
    }

    [Fact]
    public void CreateRuntime_ListeningWithoutCredentials_FallsBackToNoOp()
    {
        // Listening mode without ApiKey/BearerToken/ServerCertificate =
        // the Tentacle can't self-register. ResolveRegistrar should return
        // NoOpRegistrar with a warning instead of letting the HTTP client
        // fail with a mysterious 401.
        var settings = new TentacleSettings
        {
            ServerUrl = "https://server:7078",
            Flavor = "Tentacle"
        };

        var runtime = _flavor.CreateRuntime(BuildContext(settings));

        runtime.CommunicationMode.ShouldBe(TentacleCommunicationMode.Listening);
        runtime.Registrar.ShouldBeOfType<NoOpRegistrar>();
    }

    [Fact]
    public void CreateRuntime_AlreadyRegistered_SkipsReRegistration()
    {
        var settings = new TentacleSettings
        {
            ServerCommsUrl = "https://server:10943",
            ServerUrl = "https://server:7078",
            ServerCertificate = "AABBCCDD",
            Registered = "true",
            Flavor = "Tentacle"
        };

        var runtime = _flavor.CreateRuntime(BuildContext(settings));

        runtime.CommunicationMode.ShouldBe(TentacleCommunicationMode.Polling);
        runtime.Registrar.ShouldBeOfType<NoOpRegistrar>();
    }

    [Fact]
    public void CreateRuntime_DockerFirstRun_ServerCertificateWithoutRegisteredFlag_StillRegisters()
    {
        // Regression: Docker users pass Tentacle__ServerCertificate for TLS pinning on
        // first run. The old code treated any non-empty ServerCertificate as "already
        // registered" → skipped registration → Server never learned about the Tentacle.
        var settings = new TentacleSettings
        {
            ServerCommsUrl = "https://server:10943",
            ServerUrl = "https://server:7078",
            ServerCertificate = "AABBCCDD",
            ApiKey = "API-KEY",
            Flavor = "Tentacle"
            // Registered NOT set
        };

        var runtime = _flavor.CreateRuntime(BuildContext(settings));

        runtime.CommunicationMode.ShouldBe(TentacleCommunicationMode.Polling);
        runtime.Registrar.ShouldBeOfType<TentaclePollingRegistrar>();
    }

    [Fact]
    public async Task CreateRuntime_ListeningMode_NoServerUrl_SkipsRegistration()
    {
        var settings = new TentacleSettings
        {
            ServerUrl = "https://localhost:7078",
            ServerCertificate = "AABBCCDD",
            Flavor = "Tentacle"
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
            Flavor = "Tentacle"
        };

        var runtime = _flavor.CreateRuntime(BuildContext(settings));

        runtime.Metadata.ShouldContainKey("flavor");
        runtime.Metadata["flavor"].ShouldBe("Tentacle");
        runtime.Metadata.ShouldContainKey("os");
        runtime.Metadata.ShouldContainKey("communicationMode");
        runtime.Metadata["communicationMode"].ShouldBe("Polling");
        runtime.Metadata.ShouldContainKey("workspacePath");
    }

    [Theory]
    [InlineData("LinuxTentacle")]   // legacy section still binds — backward compat
    [InlineData("TentacleFlavor")]  // new OS-neutral section binds
    public void CreateRuntime_BindsFlavorSettings_FromLegacyAndNewConfigSection(string section)
    {
        var settings = new TentacleSettings
        {
            ServerCommsUrl = "https://server:10943",
            ServerUrl = "https://server:7078",
            Flavor = "Tentacle"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { [$"{section}:WorkspacePath"] = "/custom/work" })
            .Build();

        var runtime = _flavor.CreateRuntime(new TentacleFlavorContext { TentacleSettings = settings, Configuration = config });

        runtime.Metadata["workspacePath"].ShouldBe("/custom/work",
            customMessage: $"flavor settings must bind from the '{section}' config section (legacy + neutral both supported).");
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
