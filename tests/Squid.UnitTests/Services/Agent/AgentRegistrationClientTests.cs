using System;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Registration;

namespace Squid.UnitTests.Services.Tentacle;

public class TentacleRegistrationClientTests
{
    [Fact]
    public void Constructor_AcceptsSettings()
    {
        var tentacleSettings = new TentacleSettings
        {
            ServerUrl = "https://test-server:7078",
            BearerToken = "test-token"
        };

        var client = new TentacleRegistrationClient(tentacleSettings, new KubernetesSettings());

        client.ShouldNotBeNull();
    }

    [Fact]
    public async Task RegisterAsync_FailsAgainstNonListeningPort()
    {
        var tentacleSettings = new TentacleSettings
        {
            ServerUrl = "https://localhost:1",
            BearerToken = "test-token"
        };

        var client = new TentacleRegistrationClient(tentacleSettings, new KubernetesSettings());
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Should.ThrowAsync<Exception>(
            () => client.RegisterAsync("sub-id", "thumbprint", cts.Token));
    }

    [Fact]
    public void RegistrationResult_HasRequiredProperties()
    {
        var result = new RegistrationResult
        {
            MachineId = 42,
            ServerThumbprint = "ABC123",
            SubscriptionUri = "poll://sub-id/"
        };

        result.MachineId.ShouldBe(42);
        result.ServerThumbprint.ShouldBe("ABC123");
        result.SubscriptionUri.ShouldBe("poll://sub-id/");
    }

    [Fact]
    public void RegistrationResult_Defaults()
    {
        var result = new RegistrationResult();

        result.MachineId.ShouldBe(0);
        result.ServerThumbprint.ShouldBe(string.Empty);
        result.SubscriptionUri.ShouldBe(string.Empty);
    }

    [Fact]
    public void TentacleSettings_DefaultValues()
    {
        var settings = new TentacleSettings();

        settings.ServerUrl.ShouldBe("https://localhost:7078");
        settings.ServerPollingPort.ShouldBe(10943);
        settings.BearerToken.ShouldBe(string.Empty);
        settings.Roles.ShouldBe("k8s");
        settings.HealthCheckPort.ShouldBe(8080);
    }

    [Fact]
    public void KubernetesSettings_DefaultValues()
    {
        var settings = new KubernetesSettings();

        settings.Namespace.ShouldBe("default");
        settings.UseScriptPods.ShouldBeFalse();
        settings.ScriptPodTimeoutSeconds.ShouldBe(1800);
        settings.ScriptPodCpuRequest.ShouldBe("25m");
        settings.ScriptPodMemoryRequest.ShouldBe("100Mi");
        settings.ScriptPodCpuLimit.ShouldBe("500m");
        settings.ScriptPodMemoryLimit.ShouldBe("512Mi");
    }
}
