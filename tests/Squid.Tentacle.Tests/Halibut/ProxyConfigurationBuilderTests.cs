using System.Net;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Halibut;

namespace Squid.Tentacle.Tests.Halibut;

public class ProxyConfigurationBuilderTests
{
    [Fact]
    public void BuildHalibutProxy_NullSettings_ReturnsNull()
    {
        // null → direct connection. ServiceEndPoint accepts null proxy.
        ProxyConfigurationBuilder.BuildHalibutProxy(null).ShouldBeNull();
    }

    [Fact]
    public void BuildHalibutProxy_EmptyHost_ReturnsNull()
    {
        // IsConfigured is false → no proxy wrapper built.
        var settings = new ProxySettings { Host = "", Port = 8080 };

        ProxyConfigurationBuilder.BuildHalibutProxy(settings).ShouldBeNull();
    }

    [Fact]
    public void BuildHalibutProxy_MissingPort_ReturnsNull()
    {
        var settings = new ProxySettings { Host = "proxy.corp", Port = 0 };

        ProxyConfigurationBuilder.BuildHalibutProxy(settings).ShouldBeNull();
    }

    [Fact]
    public void BuildHalibutProxy_HostAndPort_ProducesHalibutProxyDetails()
    {
        var settings = new ProxySettings { Host = "proxy.corp", Port = 3128 };

        var proxy = ProxyConfigurationBuilder.BuildHalibutProxy(settings);

        proxy.ShouldNotBeNull();
        proxy.Host.ShouldBe("proxy.corp");
        proxy.Port.ShouldBe(3128);
    }

    [Fact]
    public void BuildHttpClientProxy_NullSettings_ReturnsNull()
    {
        ProxyConfigurationBuilder.BuildHttpClientProxy(null).ShouldBeNull();
    }

    [Fact]
    public void BuildHttpClientProxy_EmptyHost_ReturnsNull_SoHttpClientFallsBackToEnv()
    {
        ProxyConfigurationBuilder.BuildHttpClientProxy(new ProxySettings()).ShouldBeNull();
    }

    [Fact]
    public void BuildHttpClientProxy_WithHostPort_ProducesWebProxy()
    {
        var settings = new ProxySettings { Host = "proxy.corp", Port = 3128 };

        var proxy = ProxyConfigurationBuilder.BuildHttpClientProxy(settings);

        proxy.ShouldNotBeNull();
        var webProxy = proxy.ShouldBeOfType<WebProxy>();
        webProxy.Address!.ToString().ShouldBe("http://proxy.corp:3128/");
    }

    [Fact]
    public void BuildHttpClientProxy_WithCredentials_AttachesNetworkCredential()
    {
        var settings = new ProxySettings
        {
            Host = "proxy.corp",
            Port = 3128,
            Username = "bob",
            Password = "secret"
        };

        var proxy = ProxyConfigurationBuilder.BuildHttpClientProxy(settings);

        proxy.ShouldNotBeNull();
        proxy.Credentials.ShouldBeOfType<NetworkCredential>();
        var creds = (NetworkCredential)proxy.Credentials!;
        creds.UserName.ShouldBe("bob");
        creds.Password.ShouldBe("secret");
    }

    [Fact]
    public void BuildHttpClientProxy_HostPortOnly_NoCredentials_OmitsCredentialAssignment()
    {
        // Anonymous proxies are common for corporate CONNECT proxies that rely on IP allow-lists.
        var settings = new ProxySettings { Host = "proxy.corp", Port = 3128 };

        var proxy = ProxyConfigurationBuilder.BuildHttpClientProxy(settings);

        proxy.ShouldNotBeNull();
        proxy.Credentials.ShouldBeNull();
    }
}
