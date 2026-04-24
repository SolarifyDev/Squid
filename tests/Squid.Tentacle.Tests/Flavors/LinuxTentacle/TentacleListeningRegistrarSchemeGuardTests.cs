using Squid.Tentacle.Flavors.LinuxTentacle;

namespace Squid.Tentacle.Tests.Flavors.LinuxTentacle;

/// <summary>
/// P0-T.8 regression guard (2026-04-24 audit): Listening-Tentacle
/// registration sends the API key (or Bearer token) as an HTTP header.
/// If the configured <c>ServerUrl</c> is <c>http://…</c>, credentials
/// travel over the wire in cleartext — a misconfiguration invisible to
/// operators (everything "works"). Must fail-closed with an actionable
/// error, except when operator explicitly opts in via env var for
/// dev/internal-only scenarios.
///
/// <para>The env-var escape hatch name <c>SQUID_ALLOW_HTTP_REGISTER</c>
/// is pinned by <see cref="AllowHttpEnvVar_ConstantNamePinned"/> — a
/// rename breaks every air-gapped operator who set the env var by its
/// documented name.</para>
/// </summary>
public sealed class TentacleListeningRegistrarSchemeGuardTests
{
    [Fact]
    public void AllowHttpEnvVar_ConstantNamePinned()
    {
        // Renaming this constant breaks every operator who whitelisted
        // http:// for an internal deploy via the documented env var name.
        // Hard-pin in test to force the rename to be a visible decision.
        TentacleListeningRegistrar.AllowHttpRegisterEnvVar.ShouldBe("SQUID_ALLOW_HTTP_REGISTER");
    }

    [Theory]
    [InlineData("https://squid.example.com")]
    [InlineData("https://squid.example.com/")]
    [InlineData("HTTPS://SQUID.EXAMPLE.COM")]     // case-insensitive scheme
    [InlineData("https://127.0.0.1:5443")]
    public void HttpsScheme_WithSecret_PassesWithoutOptIn(string serverUrl)
    {
        Should.NotThrow(
            () => TentacleListeningRegistrar.EnsureSchemeSafeForSecret(serverUrl, hasSecret: true, allowHttpOverride: false),
            customMessage: "https URLs with secrets must pass without any opt-in");
    }

    [Theory]
    [InlineData("http://squid.example.com", "non-loopback http is always insecure — cleartext ApiKey")]
    [InlineData("http://127.0.0.1:5078", "loopback http is STILL unsafe in shared-container / multi-tenant hosts — no implicit exception")]
    [InlineData("http://localhost:5078", "same reasoning — loopback isn't a magic safety moat")]
    [InlineData("HTTP://squid.example.com", "case-insensitive scheme check")]
    public void HttpScheme_WithSecret_ThrowsUnlessOptedIn(string serverUrl, string rationale)
    {
        var thrown = Should.Throw<InvalidOperationException>(
            () => TentacleListeningRegistrar.EnsureSchemeSafeForSecret(serverUrl, hasSecret: true, allowHttpOverride: false),
            customMessage:
                $"http:// ServerUrl with a secret must throw unless opt-in is set. Rationale: {rationale}. " +
                $"Regression here means operators who misconfigure ServerUrl send their API key in cleartext.");

        thrown.Message.ShouldContain("ServerUrl",
            customMessage: "error must name the offending setting so operators know what to fix");
        thrown.Message.ShouldContain("SQUID_ALLOW_HTTP_REGISTER",
            customMessage: "error must name the opt-in env var so operators can enable it for genuine dev/internal scenarios");
    }

    [Theory]
    [InlineData("http://squid.example.com")]
    [InlineData("http://localhost:5078")]
    public void HttpScheme_WithSecret_PassesWhenOptInExplicit(string serverUrl)
    {
        // Operator opts in knowing the risk (internal network, dev env,
        // CI integration). Proceed with a warning (not asserted here —
        // logging verification would need a log sink mock; the important
        // behaviour is "doesn't throw").
        Should.NotThrow(
            () => TentacleListeningRegistrar.EnsureSchemeSafeForSecret(serverUrl, hasSecret: true, allowHttpOverride: true),
            customMessage: "explicit opt-in must allow http:// — this is the escape hatch for dev/internal networks");
    }

    [Theory]
    [InlineData("http://squid.example.com")]
    [InlineData("http://localhost:5078")]
    public void HttpScheme_WithoutSecret_PassesEvenWithoutOptIn(string serverUrl)
    {
        // If there's no ApiKey and no Bearer, there's no cleartext secret
        // to protect. Operator may be testing unauth endpoints. The guard
        // triggers ONLY when a secret is attached — fewer false positives.
        Should.NotThrow(
            () => TentacleListeningRegistrar.EnsureSchemeSafeForSecret(serverUrl, hasSecret: false, allowHttpOverride: false),
            customMessage: "http:// without secret is permitted — no cleartext-credential leak possible");
    }

    [Theory]
    [InlineData("ftp://squid.example.com")]
    [InlineData("file:///tmp/fake-server")]
    public void NonHttpScheme_AnyCredentialCombo_Throws(string serverUrl)
    {
        // ftp / file / other schemes are not valid Squid Server targets —
        // reject regardless of credentials so a typo in ServerUrl surfaces
        // at register time not at first-request time.
        Should.Throw<InvalidOperationException>(
            () => TentacleListeningRegistrar.EnsureSchemeSafeForSecret(serverUrl, hasSecret: true, allowHttpOverride: true),
            customMessage: "non-http(s) schemes must always throw — not a valid Squid Server target regardless of secrets or opt-in");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("://missing-scheme")]
    public void MalformedUrl_ThrowsWithClearMessage(string serverUrl)
    {
        // Garbage ServerUrl must surface a clean error rather than a
        // deep UriFormatException stack trace. Guard parses the URL
        // explicitly.
        Should.Throw<InvalidOperationException>(
            () => TentacleListeningRegistrar.EnsureSchemeSafeForSecret(serverUrl, hasSecret: true, allowHttpOverride: false),
            customMessage: "malformed ServerUrl must surface InvalidOperationException — not a bare UriFormatException");
    }
}
