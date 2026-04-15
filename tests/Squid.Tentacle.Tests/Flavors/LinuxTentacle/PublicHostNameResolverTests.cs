using System.Net;
using Squid.Tentacle.Flavors.LinuxTentacle;

namespace Squid.Tentacle.Tests.Flavors.LinuxTentacle;

public class PublicHostNameResolverTests
{
    // ========================================================================
    // ParseMode — handles the precedence rules between explicit config & default
    // ========================================================================

    [Theory]
    [InlineData("Custom", false, PublicHostNameMode.Custom)]
    [InlineData("ComputerName", false, PublicHostNameMode.ComputerName)]
    [InlineData("FQDN", false, PublicHostNameMode.FQDN)]
    [InlineData("PublicIp", false, PublicHostNameMode.PublicIp)]
    [InlineData("CUSTOM", false, PublicHostNameMode.Custom)]       // case-insensitive
    [InlineData("fqdn", false, PublicHostNameMode.FQDN)]
    public void ParseMode_RecognisesAllModes_CaseInsensitively(string raw, bool hasExplicitHost, PublicHostNameMode expected)
    {
        PublicHostNameResolver.ParseMode(raw, hasExplicitHost).ShouldBe(expected);
    }

    [Fact]
    public void ParseMode_EmptyWithExplicitHostName_DefaultsToCustom()
    {
        // When the user supplied --listening-host but didn't pick a mode, we should honour it.
        PublicHostNameResolver.ParseMode(null, hasExplicitHostName: true).ShouldBe(PublicHostNameMode.Custom);
        PublicHostNameResolver.ParseMode("", hasExplicitHostName: true).ShouldBe(PublicHostNameMode.Custom);
    }

    [Fact]
    public void ParseMode_EmptyWithoutExplicitHostName_DefaultsToComputerName()
    {
        // Legacy Squid behaviour — Dns.GetHostName. Preserves upgrade compatibility.
        PublicHostNameResolver.ParseMode(null, hasExplicitHostName: false).ShouldBe(PublicHostNameMode.ComputerName);
    }

    [Fact]
    public void ParseMode_GarbageValue_FallsBackGracefully()
    {
        // Invalid mode string shouldn't throw — fall through to the default logic instead.
        PublicHostNameResolver.ParseMode("NotAMode", hasExplicitHostName: true).ShouldBe(PublicHostNameMode.Custom);
        PublicHostNameResolver.ParseMode("NotAMode", hasExplicitHostName: false).ShouldBe(PublicHostNameMode.ComputerName);
    }

    // ========================================================================
    // Resolve — end-to-end host resolution per mode
    // ========================================================================

    [Fact]
    public void Resolve_Custom_ReturnsSuppliedValue()
    {
        PublicHostNameResolver.Resolve(PublicHostNameMode.Custom, "example.com")
            .ShouldBe("example.com");
    }

    [Fact]
    public void Resolve_Custom_EmptyValue_FallsBackToHostname()
    {
        // If user picks Custom but forgets the value, we don't register an empty URI —
        // we fall back to ComputerName so registration still succeeds.
        var result = PublicHostNameResolver.Resolve(PublicHostNameMode.Custom, "");

        result.ShouldBe(Dns.GetHostName());
    }

    [Fact]
    public void Resolve_ComputerName_IgnoresSuppliedValue()
    {
        // ComputerName mode means "always use Dns.GetHostName", regardless of whatever
        // ListeningHostName was set to — supports the "I changed my mind" case.
        var result = PublicHostNameResolver.Resolve(PublicHostNameMode.ComputerName, "override-ignored");

        result.ShouldBe(Dns.GetHostName());
    }

    [Fact]
    public void Resolve_FQDN_ReturnsANonEmptyString()
    {
        // Can't assert exact value (it depends on the test machine's reverse DNS),
        // but the contract says this mode must never return null/empty.
        var result = PublicHostNameResolver.Resolve(PublicHostNameMode.FQDN, null);

        result.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Resolve_PublicIp_FallsBackToHostname_WhenNotInCloud()
    {
        // Running the tests outside AWS/Azure/GCE — all metadata probes fail, so we should
        // fall back to the short hostname rather than returning null or throwing.
        var result = PublicHostNameResolver.Resolve(PublicHostNameMode.PublicIp, null);

        result.ShouldNotBeNullOrWhiteSpace();
        // Shouldn't look like an AWS 169.254.169.254 error leaking through
        result.ShouldNotContain("169.254");
    }
}
