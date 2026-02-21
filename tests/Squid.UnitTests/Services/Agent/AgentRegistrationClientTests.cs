using System;
using Squid.Agent.Configuration;
using Squid.Agent.Registration;

namespace Squid.UnitTests.Services.Agent;

public class AgentRegistrationClientTests
{
    [Fact]
    public void Constructor_AcceptsSettings()
    {
        var settings = new AgentSettings
        {
            ServerUrl = "https://test-server:7078",
            BearerToken = "test-token"
        };

        var client = new AgentRegistrationClient(settings);

        client.ShouldNotBeNull();
    }

    [Fact]
    public async Task RegisterAsync_FailsAgainstNonListeningPort()
    {
        var settings = new AgentSettings
        {
            ServerUrl = "https://localhost:1",
            BearerToken = "test-token"
        };

        var client = new AgentRegistrationClient(settings);
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
    public void AgentSettings_DefaultValues()
    {
        var settings = new AgentSettings();

        settings.ServerUrl.ShouldBe("https://localhost:7078");
        settings.ServerPollingPort.ShouldBe(10943);
        settings.BearerToken.ShouldBe(string.Empty);
        settings.Namespace.ShouldBe("default");
        settings.Roles.ShouldBe("k8s");
        settings.UseScriptPods.ShouldBeFalse();
        settings.ScriptPodTimeoutSeconds.ShouldBe(1800);
        settings.ScriptPodCpuRequest.ShouldBe("25m");
        settings.ScriptPodMemoryRequest.ShouldBe("100Mi");
        settings.ScriptPodCpuLimit.ShouldBe("500m");
        settings.ScriptPodMemoryLimit.ShouldBe("512Mi");
        settings.HealthCheckPort.ShouldBe(8080);
    }
}
