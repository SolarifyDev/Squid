using Squid.Tentacle.Halibut;
using Squid.Tentacle.Tests.Support;

namespace Squid.Tentacle.Tests.Halibut;

[Trait("Category", TentacleTestCategories.Core)]
public class TentacleHalibutHostUriTests
{
    [Fact]
    public void ResolvePollUri_FallsBack_When_SubscriptionUri_Is_Missing()
    {
        var uri = TentacleHalibutHost.ResolvePollUri("abc123", null);

        uri.ShouldBe(new Uri("poll://abc123/"));
    }

    [Fact]
    public void ResolvePollUri_Uses_ServerReturned_SubscriptionUri()
    {
        var uri = TentacleHalibutHost.ResolvePollUri("ignored", "poll://server-sub/");

        uri.ShouldBe(new Uri("poll://server-sub/"));
    }

    [Fact]
    public void ResolvePollUri_Throws_On_Invalid_SubscriptionUri()
    {
        Should.Throw<UriFormatException>(() => TentacleHalibutHost.ResolvePollUri("abc123", "not a uri"));
    }

    [Fact]
    public void BuildPollingEndpointUri_Uses_Server_Host_And_Polling_Port()
    {
        var uri = TentacleHalibutHost.BuildPollingEndpointUri("https://squid.example.com:7078/some/path", 10943);

        uri.ShouldBe(new Uri("https://squid.example.com:10943/"));
    }
}
