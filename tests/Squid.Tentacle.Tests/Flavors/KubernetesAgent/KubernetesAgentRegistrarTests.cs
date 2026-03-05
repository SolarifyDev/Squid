using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Fakes;

namespace Squid.Tentacle.Tests.Flavors.KubernetesAgent;

[Trait("Category", TentacleTestCategories.Flavor)]
public class KubernetesAgentRegistrarTests : TimedTestBase
{
    [Fact]
    public async Task RegisterAsync_Maps_Server_Response_To_TentacleRegistration()
    {
        await using var server = FakeMachineRegistrationServer.Start();

        var registrar = new Squid.Tentacle.Flavors.KubernetesAgent.KubernetesAgentRegistrar(
            new TentacleSettings
            {
                ServerUrl = server.BaseAddress.ToString().TrimEnd('/'),
                BearerToken = "reg-token",
                MachineName = "agent-01",
                Roles = "web",
                Environments = "Test,Production",
                SpaceId = 9,
                AgentVersion = "1.0.3"
            },
            new KubernetesSettings
            {
                Namespace = "apps"
            });

        var result = await registrar.RegisterAsync(
            new TentacleIdentity("sub-123", "thumb-123"),
            TestCancellationToken);

        result.MachineId.ShouldBe(1234);
        result.ServerThumbprint.ShouldBe("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        result.SubscriptionUri.ShouldBe("poll://sub-123/");
        server.LastAuthorizationHeader.ShouldBe("Bearer reg-token");
        server.LastRequestBody.ShouldContain("\"spaceId\":9");
        server.LastRequestBody.ShouldContain("\"namespace\":\"apps\"");
        server.LastRequestBody.ShouldContain("\"subscriptionId\":\"sub-123\"");
        server.LastRequestBody.ShouldContain("\"agentVersion\":\"1.0.3\"");
    }
}
