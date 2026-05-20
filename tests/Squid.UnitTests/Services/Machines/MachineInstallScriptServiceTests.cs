using System.Net;
using Squid.Core.Services.Account;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Machines.Scripts.Tentacle;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Message.Commands.Account;
using Squid.Message.Commands.Machine;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.Machines;

public class MachineInstallScriptServiceTests
{
    private readonly Mock<IAccountService> _accountService = new();
    private readonly Mock<IMachineDataProvider> _machineDataProvider = new();
    private readonly Mock<IAgentVersionProvider> _agentVersionProvider = new();
    private readonly Mock<ITentacleCommsUrlProbe> _commsUrlProbe = new();
    private readonly Mock<ITentacleVersionRegistry> _versionRegistry = new();
    private readonly MachineScriptService _service;

    public MachineInstallScriptServiceTests()
    {
        _accountService
            .Setup(x => x.CreateApiKeyAsync(CurrentUsers.InternalUser.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateApiKeyResponseData { ApiKey = "test-api-key" });

        // Default: probe skipped — individual tests that care can override.
        _commsUrlProbe
            .Setup(x => x.ProbeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TentacleCommsProbeResult { Skipped = true, Detail = "Stubbed for unit test" });

        _versionRegistry
            .Setup(x => x.GetLatestVersionAsync(It.IsAny<string>(), It.IsAny<MachineRuntimeCapabilities>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("1.6.0");

        _service = new MachineScriptService(
            _accountService.Object,
            _machineDataProvider.Object,
            _agentVersionProvider.Object,
            new Squid.Core.Settings.SelfCert.SelfCertSetting(),
            [],
            _commsUrlProbe.Object,
            _versionRegistry.Object);
    }

    private static GenerateKubernetesAgentInstallScriptCommand CreateCommand(
        string agentName = "my-agent",
        string serverUrl = "https://squid.example.com",
        string serverCommsUrl = "https://squid.example.com:10943",
        string storageType = null,
        string nfsServer = null,
        string nfsPath = null,
        string storageClassName = null)
    {
        return new GenerateKubernetesAgentInstallScriptCommand
        {
            AgentName = agentName,
            ServerUrl = serverUrl,
            ServerCommsUrl = serverCommsUrl,
            Environments = ["Test", "Production"],
            Tags = ["k8s", "web"],
            SpaceId = 1,
            StorageType = storageType,
            NfsServer = nfsServer,
            NfsPath = nfsPath,
            StorageClassName = storageClassName
        };
    }

    private async Task<GenerateKubernetesAgentInstallScriptData> GenerateSuccessDataAsync(
        GenerateKubernetesAgentInstallScriptCommand command = null)
    {
        var response = await _service.GenerateKubernetesAgentInstallScriptAsync(
            command ?? CreateCommand(),
            CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.ShouldNotBeNull();
        return response.Data;
    }

    [Fact]
    public async Task GenerateScript_ReturnsSubscriptionId_As32CharGuid()
    {
        var result = await GenerateSuccessDataAsync();

        result.SubscriptionId.ShouldNotBeNullOrWhiteSpace();
        result.SubscriptionId.Length.ShouldBe(32);
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsHelmUpgradeInstall()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldStartWith("helm upgrade --install --rollback-on-failure");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ReleaseNameIsSafe()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain($"squid-agent-{result.SubscriptionId[..8]}");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsMachineName()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain("tentacle.machineName=\"my-agent\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsNamespace()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain("--create-namespace --namespace squid-agent");
        result.AgentInstallScript.ShouldContain("--version \"1.*.*\"");
    }

    [Theory]
    [InlineData("production", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public async Task GenerateScript_AgentInstallScript_DefaultNamespaceOnlyWhenProvided(string defaultNamespace, bool shouldContain)
    {
        var command = CreateCommand();
        command.DefaultNamespace = defaultNamespace;

        var result = await GenerateSuccessDataAsync(command);

        if (shouldContain)
            result.AgentInstallScript.ShouldContain("kubernetes.namespace=\"production\"");
        else
            result.AgentInstallScript.ShouldNotContain("kubernetes.namespace");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsOciRegistry()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain("tentacle.chartRef=\"oci://registry-1.docker.io/squidcd/kubernetes-agent\"");
        result.AgentInstallScript.ShouldContain("oci://registry-1.docker.io/squidcd/kubernetes-agent");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_UsesCustomChartRef_WhenProvided()
    {
        const string customChartRef = "oci://registry.example.com/squid/kubernetes-agent";
        var command = CreateCommand();
        command.ChartRef = customChartRef;

        var result = await GenerateSuccessDataAsync(command);

        result.AgentInstallScript.ShouldContain($"tentacle.chartRef=\"{customChartRef}\"");
        result.AgentInstallScript.ShouldContain(customChartRef);
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsServerUrls()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain("tentacle.serverUrl=\"https://squid.example.com\"");
        result.AgentInstallScript.ShouldContain("tentacle.serverCommsUrl=\"https://squid.example.com:10943\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsApiKey()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain("tentacle.apiKey=\"test-api-key\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsRolesAndEnvironmentsAsHelmArrays()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain("tentacle.roles=\"{k8s,web}\"");
        result.AgentInstallScript.ShouldContain("tentacle.environments=\"{Test,Production}\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_SingleItemArrays()
    {
        var command = CreateCommand();
        command.Tags = ["web"];
        command.Environments = ["Test"];

        var result = await GenerateSuccessDataAsync(command);

        result.AgentInstallScript.ShouldContain("tentacle.roles=\"{web}\"");
        result.AgentInstallScript.ShouldContain("tentacle.environments=\"{Test}\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_EmptyArrays()
    {
        var command = CreateCommand();
        command.Tags = [];
        command.Environments = [];

        var result = await GenerateSuccessDataAsync(command);

        result.AgentInstallScript.ShouldContain("tentacle.roles=\"{}\"");
        result.AgentInstallScript.ShouldContain("tentacle.environments=\"{}\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsSubscriptionId()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain($"tentacle.subscriptionId=\"{result.SubscriptionId}\"");
    }

    [Fact]
    public async Task GenerateScript_AgentInstallScript_ContainsFlavorAndSpaceId()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain("tentacle.flavor=\"KubernetesAgent\"");
        result.AgentInstallScript.ShouldContain("tentacle.spaceId=\"1\"");
    }

    [Fact]
    public async Task GenerateScript_EmptyAgentName_FallsBackToReleaseName()
    {
        var result = await GenerateSuccessDataAsync(CreateCommand(agentName: ""));

        var releaseName = $"squid-agent-{result.SubscriptionId[..8]}";
        result.AgentInstallScript.ShouldContain($"tentacle.machineName=\"{releaseName}\"");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("builtin-nfs", true)]
    [InlineData("external-nfs", false)]
    [InlineData("custom", false)]
    public async Task GenerateScript_NfsCsiDriverScript_ConditionalOnStorageType(string storageType, bool shouldHaveCsiScript)
    {
        var command = CreateCommand(storageType: storageType);
        var result = await GenerateSuccessDataAsync(command);

        if (shouldHaveCsiScript)
        {
            result.NfsCsiDriverScript.ShouldNotBeNull();
            result.NfsCsiDriverScript.ShouldContain("helm upgrade --install --rollback-on-failure");
            result.NfsCsiDriverScript.ShouldContain("csi-driver-nfs");
        }
        else
        {
            result.NfsCsiDriverScript.ShouldBeNull();
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("builtin-nfs", true)]
    [InlineData("external-nfs", false)]
    [InlineData("custom", false)]
    public async Task GenerateScript_NfsCsiDriverRequired_MatchesStorageType(string storageType, bool expectedRequired)
    {
        var command = CreateCommand(storageType: storageType);
        var result = await GenerateSuccessDataAsync(command);

        result.NfsCsiDriverRequired.ShouldBe(expectedRequired);
    }

    [Fact]
    public async Task GenerateScript_ExternalNfs_IncludesNfsServerAndPath()
    {
        var command = CreateCommand(storageType: "external-nfs", nfsServer: "10.0.0.5", nfsPath: "/exports/data");
        var result = await GenerateSuccessDataAsync(command);

        result.AgentInstallScript.ShouldContain("workspace.nfs.server=\"10.0.0.5\"");
        result.AgentInstallScript.ShouldContain("workspace.nfs.path=\"/exports/data\"");
    }

    [Fact]
    public async Task GenerateScript_ExternalNfs_OmitsPathWhenNotProvided()
    {
        var command = CreateCommand(storageType: "external-nfs", nfsServer: "10.0.0.5");
        var result = await GenerateSuccessDataAsync(command);

        result.AgentInstallScript.ShouldContain("workspace.nfs.server=\"10.0.0.5\"");
        result.AgentInstallScript.ShouldNotContain("workspace.nfs.path");
    }

    [Fact]
    public async Task GenerateScript_CustomPvc_IncludesStorageClassName()
    {
        var command = CreateCommand(storageType: "custom", storageClassName: "gp3");
        var result = await GenerateSuccessDataAsync(command);

        result.AgentInstallScript.ShouldContain("workspace.storageClassName=\"gp3\"");
    }

    [Fact]
    public async Task GenerateScript_BuiltinNfs_NoExtraStorageValues()
    {
        var command = CreateCommand(storageType: "builtin-nfs");
        var result = await GenerateSuccessDataAsync(command);

        result.AgentInstallScript.ShouldNotContain("workspace.nfs.server");
        result.AgentInstallScript.ShouldNotContain("workspace.storageClassName");
    }

    [Fact]
    public async Task GenerateScript_UsesBackslashLineContinuation()
    {
        var result = await GenerateSuccessDataAsync();

        result.AgentInstallScript.ShouldContain(" \\\n");
        result.NfsCsiDriverScript.ShouldContain(" \\\n");
    }

    [Fact]
    public async Task GenerateScript_ApiKeyCreationFails_ReturnsInternalServerError()
    {
        // Force FindApiKeyByDescriptionAsync to return null (no shared key yet) so we
        // exercise the mint path; then have CreateApiKeyAsync return an empty key to
        // simulate failure.
        _accountService
            .Setup(x => x.FindApiKeyByDescriptionAsync(CurrentUsers.InternalUser.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApiKeyWithSecret?)null);
        _accountService
            .Setup(x => x.CreateApiKeyAsync(CurrentUsers.InternalUser.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateApiKeyResponseData { ApiKey = "" });

        var response = await _service.GenerateKubernetesAgentInstallScriptAsync(CreateCommand(), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.InternalServerError);
        response.Msg.ShouldContain("Failed to create Kubernetes Agent bootstrap API key",
            customMessage: "Error message must name the specific surface (bootstrap key) so operators know where to look.");
        response.Data.ShouldBeNull();
    }

    [Fact]
    public async Task GenerateScript_SharedKeyAlreadyExists_ReusesItWithoutMinting()
    {
        // Single-instance pin: when FindApiKeyByDescriptionAsync returns an existing
        // key, the service MUST NOT call CreateApiKeyAsync. DB must not accumulate
        // one row per install-script call.
        const string sharedKey = "SHARED-K8S-KEY-VALUE";
        _accountService
            .Setup(x => x.FindApiKeyByDescriptionAsync(CurrentUsers.InternalUser.Id, MachineScriptService.KubernetesAgentBootstrapKeyDescription, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiKeyWithSecret(99, sharedKey, MachineScriptService.KubernetesAgentBootstrapKeyDescription, DateTimeOffset.UtcNow));

        var response = await _service.GenerateKubernetesAgentInstallScriptAsync(CreateCommand(), CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        _accountService.Verify(x => x.CreateApiKeyAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, failMessage: "MUST NOT mint a new key when a shared bootstrap key already exists. " +
                                     "Mint per call was the 1.6.x bug -- DB accumulated one row per install-script call.");
    }

    [Fact]
    public async Task GenerateScript_NoSharedKey_MintsExactlyOneNewKey()
    {
        // Mint-once pin: when no shared key exists, CreateApiKeyAsync is called
        // exactly once with the canonical description.
        _accountService
            .Setup(x => x.FindApiKeyByDescriptionAsync(CurrentUsers.InternalUser.Id, MachineScriptService.KubernetesAgentBootstrapKeyDescription, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApiKeyWithSecret?)null);

        await _service.GenerateKubernetesAgentInstallScriptAsync(CreateCommand(), CancellationToken.None);

        _accountService.Verify(x => x.CreateApiKeyAsync(
            CurrentUsers.InternalUser.Id,
            MachineScriptService.KubernetesAgentBootstrapKeyDescription,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========================================================================
    // Polling URL probe — attaches SLB/DNS diagnostic to Tentacle install scripts
    // so operators catch misconfigurations before shipping scripts to users.
    // ========================================================================

    [Fact]
    public async Task GenerateTentacleInstallScript_PollingMode_InvokesProbeAndSurfacesResult()
    {
        _commsUrlProbe
            .Setup(x => x.ProbeAsync("https://polling.example.com:10943", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TentacleCommsProbeResult
            {
                Reachable = true,
                ThumbprintMatches = true,
                ObservedThumbprint = "FAF04764",
                Detail = "Reachable. Server cert thumbprint matches (FAF04764)"
            });

        var response = await _service.GenerateTentacleInstallScriptAsync(new GenerateTentacleInstallScriptCommand
        {
            MachineName = "mars-mac",
            ServerUrl = "https://squid-api.example.com",
            ServerCommsUrl = "https://polling.example.com:10943",
            CommunicationMode = "Polling",
            SpaceId = 1
        }, CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.CommsUrlProbe.ShouldNotBeNull();
        response.Data.CommsUrlProbe.Reachable.ShouldBeTrue();
        response.Data.CommsUrlProbe.ThumbprintMatches.ShouldBeTrue();
        response.Data.CommsUrlProbe.ObservedThumbprint.ShouldBe("FAF04764");

        _commsUrlProbe.Verify(
            x => x.ProbeAsync("https://polling.example.com:10943", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GenerateTentacleInstallScript_ListeningMode_SkipsProbe()
    {
        // Listening tentacles have no polling URL — probing is meaningless.
        var response = await _service.GenerateTentacleInstallScriptAsync(new GenerateTentacleInstallScriptCommand
        {
            MachineName = "listening-tentacle",
            ServerUrl = "https://squid-api.example.com",
            ServerCommsUrl = "",
            CommunicationMode = "Listening",
            ListeningHostName = "tentacle.example.com",
            ListeningPort = 10933,
            SpaceId = 1
        }, CancellationToken.None);

        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.CommsUrlProbe.ShouldNotBeNull();
        response.Data.CommsUrlProbe.Skipped.ShouldBeTrue();
        response.Data.CommsUrlProbe.Detail.ShouldContain("Listening");

        _commsUrlProbe.Verify(
            x => x.ProbeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateTentacleInstallScript_UnreachableComms_SurfacesDiagnosticInResponse()
    {
        // This is the failure mode we want to catch at script-generation time
        // instead of at first-agent-handshake time.
        _commsUrlProbe
            .Setup(x => x.ProbeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TentacleCommsProbeResult
            {
                Reachable = false,
                Detail = "SLB accepted TCP but dropped the TLS handshake. Verify: ... 100.64.0.0/10 ..."
            });

        var response = await _service.GenerateTentacleInstallScriptAsync(new GenerateTentacleInstallScriptCommand
        {
            MachineName = "mars-mac",
            ServerUrl = "https://squid-api.example.com",
            ServerCommsUrl = "https://polling.example.com:10943",
            CommunicationMode = "Polling",
            SpaceId = 1
        }, CancellationToken.None);

        // Script still generated — probe is advisory, not blocking — but the
        // diagnostic flows through so the UI can render a warning banner.
        response.Code.ShouldBe(HttpStatusCode.OK);
        response.Data.CommsUrlProbe.Reachable.ShouldBeFalse();
        response.Data.CommsUrlProbe.Detail.ShouldContain("100.64.0.0/10");
    }
}
