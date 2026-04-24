using Squid.Tentacle.Configuration;

namespace Squid.Tentacle.Tests.Configuration;

/// <summary>
/// P0-T.6 regression guard (2026-04-24 audit). Pre-fix, two call sites —
/// <c>TentacleListeningRegistrar.RegisterAsync</c> and <c>RegisterCommand.ExecuteAsync</c> —
/// compared <c>settings.ServerUrl</c> against the magic literal
/// <c>"https://localhost:7078"</c> to decide "operator hasn't configured a server, skip
/// auto-registration". Two problems:
///
/// <list type="number">
///   <item>The literal appeared verbatim in two production files plus the default in
///         <see cref="TentacleSettings.ServerUrl"/>. A rename of the default in one
///         place without the others drifts the sentinel: a deploy where someone updated
///         the default would silently register against a garbage URL or silently skip
///         against a real URL. Changes in one file would leave the other stale.</item>
///   <item>An operator legitimately running a dev Squid Server at
///         <c>https://localhost:7078</c> couldn't register — the tentacle treated that
///         URL as "unconfigured" and skipped the call.</item>
/// </list>
///
/// <para>Fix: one named constant + one helper, shared across both call sites. These
/// tests pin both — renaming the constant or widening/narrowing the helper's decision
/// matrix requires deliberate test updates, not a silent refactor.</para>
/// </summary>
public sealed class TentacleSettingsSentinelTests
{
    [Fact]
    public void DefaultServerUrlSentinel_Pinned()
    {
        // This literal also appears in appsettings.json and TentacleSettings.ServerUrl
        // default. Changing it without updating all three breaks operators whose first-
        // run config was written against the template. Pin it so the rename becomes a
        // compile-visible decision.
        TentacleSettings.DefaultServerUrlSentinel.ShouldBe("https://localhost:7078");
    }

    [Fact]
    public void DefaultServerUrl_MatchesSentinel()
    {
        // The default `ServerUrl` property value MUST equal the sentinel — otherwise a
        // freshly-instantiated TentacleSettings would not be flagged as "unconfigured"
        // and the registrar would try to POST to a non-existent server.
        new TentacleSettings().ServerUrl.ShouldBe(TentacleSettings.DefaultServerUrlSentinel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("https://localhost:7078")]
    public void IsAutoRegistrationUnconfigured_SentinelOrBlank_ReturnsTrue(string? serverUrl)
    {
        TentacleSettings.IsAutoRegistrationUnconfigured(serverUrl).ShouldBeTrue(
            customMessage:
                $"'{serverUrl ?? "<null>"}' must be treated as unconfigured — the registrar " +
                "must skip the HTTP POST rather than connect to a bogus endpoint.");
    }

    [Theory]
    [InlineData("https://squid.example.com")]
    [InlineData("https://squid.example.com/")]
    [InlineData("https://127.0.0.1:5443")]
    [InlineData("http://192.168.1.10:8080")]
    [InlineData("https://localhost:9999")]        // localhost but NOT the sentinel port
    [InlineData("http://localhost:7078")]          // http scheme but NOT the exact https sentinel
    public void IsAutoRegistrationUnconfigured_RealServerUrl_ReturnsFalse(string serverUrl)
    {
        // Crucially, `http://localhost:7078` and `https://localhost:9999` MUST be treated
        // as real URLs. The legitimate dev workflow "I'm running Squid Server on a local
        // port" was broken pre-fix because only the exact `https://localhost:7078` magic
        // literal was recognised.
        TentacleSettings.IsAutoRegistrationUnconfigured(serverUrl).ShouldBeFalse(
            customMessage:
                $"'{serverUrl}' is a legitimate server URL — registrar must attempt the POST. " +
                "Treating any URL other than the exact sentinel as 'unconfigured' would break " +
                "local-dev deploys that happen to use localhost.");
    }
}
