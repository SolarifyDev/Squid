using System.Net;
using Shouldly;
using Squid.Tentacle.Halibut;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Halibut;

/// <summary>
/// P1-T.10 (Phase-8): pin the env-var-driven listen-IP override surface.
///
/// <para>Pre-fix the listening Halibut runtime called
/// <c>_runtime.Listen(port)</c>, which binds to <c>IPAddress.Any</c>
/// (0.0.0.0) — every interface on the host is reachable. Multi-NIC
/// servers and strict-firewall environments need to pin to a specific
/// interface (often loopback for sidecar tentacles, or a private NIC
/// for multi-homed boxes). Pre-fix there was no escape hatch — operator
/// would have to fork.</para>
///
/// <para>Post-fix: <c>SQUID_TENTACLE_LISTEN_IP_ADDRESS</c> selects the
/// bind address. Default / "any" / unset → <c>IPAddress.Any</c> (matches
/// pre-fix); a valid IP literal pins to that interface; anything
/// unparseable falls back to <c>IPAddress.Any</c> with a structured
/// warning (typo never silently makes the agent invisible).</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public class TentacleHalibutHostListenIpTests
{
    [Fact]
    public void ListenIpAddressEnvVar_ConstantNamePinned()
    {
        TentacleHalibutHost.ListenIpAddressEnvVar.ShouldBe("SQUID_TENTACLE_LISTEN_IP_ADDRESS");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("any")]
    [InlineData("ANY")]
    [InlineData("Any")]
    public void ParseListenIpAddress_DefaultOrAny_ReturnsIpAddressAny(string raw)
    {
        var ip = TentacleHalibutHost.ParseListenIpAddress(raw);

        ip.ShouldBe(IPAddress.Any,
            customMessage: $"raw='{raw ?? "<null>"}' must resolve to IPAddress.Any (matches pre-fix Listen(port) behaviour).");
    }

    [Theory]
    [InlineData("127.0.0.1")]              // loopback
    [InlineData("192.168.1.5")]             // typical private LAN
    [InlineData("10.0.0.42")]
    [InlineData("0.0.0.0")]                 // explicit any
    public void ParseListenIpAddress_ValidIPv4_ReturnsParsed(string raw)
    {
        var ip = TentacleHalibutHost.ParseListenIpAddress(raw);

        ip.ShouldNotBeNull();
        ip.ToString().ShouldBe(raw);
    }

    [Theory]
    [InlineData("::1")]                     // IPv6 loopback
    [InlineData("2001:db8::1")]              // IPv6 documentation prefix
    public void ParseListenIpAddress_ValidIPv6_ReturnsParsed(string raw)
    {
        var ip = TentacleHalibutHost.ParseListenIpAddress(raw);

        ip.ShouldNotBeNull();
        ip.AddressFamily.ShouldBe(System.Net.Sockets.AddressFamily.InterNetworkV6);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("999.999.999.999")]
    [InlineData("garbage")]
    [InlineData("hello world")]
    // Note: "127.0.0" IS parseable by IPAddress.TryParse as 127.0.0.0
    // (legacy classful interpretation in .NET); not a "garbage" case.
    public void ParseListenIpAddress_Garbage_FallsBackToIpAddressAny_NoCrash(string raw)
    {
        // Critical: an env-var typo MUST NOT crash the agent. Falling back
        // to Any is strictly safer than refusing to bind — the agent stays
        // reachable on all interfaces (operator may want to fix the typo,
        // but the agent works in the meantime).
        var ip = TentacleHalibutHost.ParseListenIpAddress(raw);

        ip.ShouldBe(IPAddress.Any,
            customMessage: $"raw='{raw}' is unparseable; must fall back to IPAddress.Any (NOT throw / NOT bind to garbage).");
    }
}
