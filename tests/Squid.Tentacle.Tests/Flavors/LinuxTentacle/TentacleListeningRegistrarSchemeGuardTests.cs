using Squid.Message.Hardening;
using Squid.Tentacle.Flavors.LinuxTentacle;

namespace Squid.Tentacle.Tests.Flavors.LinuxTentacle;

/// <summary>
/// P0-T.8 regression guard, refactored under the project-wide three-mode
/// hardening pattern (CLAUDE.md §"Hardening Three-Mode Enforcement").
///
/// <para>Listening-Tentacle registration sends the API key (or Bearer token)
/// as an HTTP header. If the configured <c>ServerUrl</c> is <c>http://</c>,
/// credentials travel over the wire in cleartext — invisible misconfiguration
/// where everything appears to "work".</para>
///
/// <para>Behaviour for the http+secret case depends on
/// <see cref="EnforcementMode"/> resolved from
/// <see cref="TentacleListeningRegistrar.EnforcementEnvVar"/>: Off (silent),
/// Warn (default — accept + warning, preserves backward compat), Strict
/// (reject + throw). Empty / non-absolute / non-http(s) URLs always throw,
/// regardless of mode — those are unconditional config errors.</para>
/// </summary>
public sealed class TentacleListeningRegistrarSchemeGuardTests
{
    [Fact]
    public void EnforcementEnvVar_ConstantNamePinned()
    {
        TentacleListeningRegistrar.EnforcementEnvVar.ShouldBe("SQUID_REGISTER_HTTPS_ENFORCEMENT");
    }

    // ── https ALWAYS passes (mode-independent) ──────────────────────────────

    [Theory]
    [InlineData(EnforcementMode.Off,    "https://squid.example.com")]
    [InlineData(EnforcementMode.Warn,   "https://squid.example.com/")]
    [InlineData(EnforcementMode.Strict, "HTTPS://SQUID.EXAMPLE.COM")]
    [InlineData(EnforcementMode.Strict, "https://127.0.0.1:5443")]
    public void HttpsScheme_WithSecret_PassesInAnyMode(EnforcementMode mode, string serverUrl)
    {
        Should.NotThrow(
            () => TentacleListeningRegistrar.EnsureSchemeSafeForSecret(serverUrl, hasSecret: true, mode: mode));
    }

    // ── http + no secret: always passes (no leak risk) ──────────────────────

    [Theory]
    [InlineData(EnforcementMode.Off,    "http://squid.example.com")]
    [InlineData(EnforcementMode.Warn,   "http://localhost:5078")]
    [InlineData(EnforcementMode.Strict, "http://squid.example.com")]
    public void HttpScheme_WithoutSecret_PassesInAnyMode(EnforcementMode mode, string serverUrl)
    {
        Should.NotThrow(
            () => TentacleListeningRegistrar.EnsureSchemeSafeForSecret(serverUrl, hasSecret: false, mode: mode),
            customMessage: "http without secret has no cleartext credential — no risk to enforce");
    }

    // ── http + secret: mode-dependent ───────────────────────────────────────

    [Theory]
    [InlineData("http://squid.example.com", "non-loopback http with cleartext ApiKey")]
    [InlineData("http://127.0.0.1:5078",   "loopback isn't a magic safety moat in shared / multi-tenant hosts")]
    [InlineData("http://localhost:5078",   "same reasoning — localhost isn't safer than non-loopback")]
    [InlineData("HTTP://squid.example.com", "case-insensitive scheme")]
    public void Strict_HttpWithSecret_Throws(string serverUrl, string rationale)
    {
        var thrown = Should.Throw<InvalidOperationException>(
            () => TentacleListeningRegistrar.EnsureSchemeSafeForSecret(
                serverUrl, hasSecret: true, mode: EnforcementMode.Strict),
            customMessage:
                $"Strict mode must reject http+secret. Rationale: {rationale}. Operators set " +
                "SQUID_REGISTER_HTTPS_ENFORCEMENT=strict for production hardening — they expect " +
                "this to fail loud.");

        thrown.Message.ShouldContain("ServerUrl");
        thrown.Message.ShouldContain(TentacleListeningRegistrar.EnforcementEnvVar);
    }

    [Theory]
    [InlineData("http://squid.example.com")]
    [InlineData("http://localhost:5078")]
    public void Warn_HttpWithSecret_DoesNotThrow_BackwardCompat(string serverUrl)
    {
        // Warn mode (default) preserves pre-Phase-1 behaviour for any deploy
        // that hasn't yet set SQUID_REGISTER_HTTPS_ENFORCEMENT. Operator sees
        // structured warning in logs.
        Should.NotThrow(
            () => TentacleListeningRegistrar.EnsureSchemeSafeForSecret(
                serverUrl, hasSecret: true, mode: EnforcementMode.Warn),
            customMessage:
                "Warn mode must NOT throw on http+secret. Pre-Phase-3 strict-by-default " +
                "broke deploys that ship with http listening URLs (internal networks).");
    }

    [Theory]
    [InlineData("http://squid.example.com")]
    [InlineData("http://localhost:5078")]
    public void Off_HttpWithSecret_AcceptsSilently(string serverUrl)
    {
        Should.NotThrow(
            () => TentacleListeningRegistrar.EnsureSchemeSafeForSecret(
                serverUrl, hasSecret: true, mode: EnforcementMode.Off));
    }

    // ── Unconditional rejections (mode-independent) ─────────────────────────

    [Theory]
    [InlineData(EnforcementMode.Off,    "ftp://squid.example.com")]
    [InlineData(EnforcementMode.Warn,   "file:///tmp/fake-server")]
    [InlineData(EnforcementMode.Strict, "gopher://example.com")]
    public void NonHttpScheme_AlwaysThrows(EnforcementMode mode, string serverUrl)
    {
        // Non-http(s) is never a valid Squid Server target. No mode can wave it
        // through — there's no "accept with warning" semantics for an unsupported
        // scheme; the request would fail at HttpClient construction anyway.
        var thrown = Should.Throw<InvalidOperationException>(
            () => TentacleListeningRegistrar.EnsureSchemeSafeForSecret(
                serverUrl, hasSecret: true, mode: mode));

        thrown.Message.ShouldContain("Unconditional",
            customMessage: "error must explain why this rejection ignores the enforcement mode");
    }

    [Theory]
    [InlineData(EnforcementMode.Off,    null)]
    [InlineData(EnforcementMode.Warn,   "")]
    [InlineData(EnforcementMode.Strict, "   ")]
    [InlineData(EnforcementMode.Warn,   "not a url")]
    [InlineData(EnforcementMode.Off,    "://missing-scheme")]
    public void MalformedOrEmptyUrl_AlwaysThrows(EnforcementMode mode, string serverUrl)
    {
        // Empty / non-absolute / unparseable URLs cannot reach any server
        // regardless of mode. Surface a clean InvalidOperationException rather
        // than a deep UriFormatException stack later.
        Should.Throw<InvalidOperationException>(
            () => TentacleListeningRegistrar.EnsureSchemeSafeForSecret(
                serverUrl, hasSecret: true, mode: mode));
    }
}
