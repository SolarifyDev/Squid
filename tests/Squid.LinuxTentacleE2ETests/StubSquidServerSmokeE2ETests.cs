using Squid.LinuxTentacleE2ETests.Infrastructure;

namespace Squid.LinuxTentacleE2ETests;

/// <summary>
/// Phase 12.M.L.D — smoke tests for the
/// <see cref="StubSquidServer"/> infrastructure port. Tier
/// 🔵 fixture-only (Rule 12). Does NOT count toward production E2E
/// coverage; validates the shared infrastructure works on Linux
/// before downstream Linux deploy E2E tests consume it.
///
/// <para><b>Why a smoke layer matters</b>: porting StubSquidServer
/// from <c>Squid.WindowsTentacleE2ETests</c> to the Linux project
/// requires Halibut + System.Net.HttpListener + X509Certificate2 —
/// all cross-platform .NET libraries that COULD have OS-specific
/// gotchas. A dedicated smoke test surfaces these issues BEFORE
/// the bigger downstream Linux deploy E2E tests start using the
/// stub — much easier to debug "stub failed to start" at smoke
/// vs "deploy test mysteriously failed" downstream.</para>
///
/// <para>Mirrors the Windows
/// <c>StubSquidServerSmokeE2ETests</c> pattern in concept; the
/// Linux smoke confirms the same lifecycle works on Linux test
/// hosts (Halibut polling + REST listener startup + clean shutdown).</para>
///
/// <para><b>Why a separate stub from the existing slim
/// <see cref="LinuxStubSquidServer"/></b>: the slim version is
/// REST-only (~190 lines) and used by Section C/D register tests.
/// StubSquidServer adds the full Halibut polling listener
/// (~600 lines) needed for deploy E2E. Both coexist for now;
/// future refactor may extract to a shared cross-OS infra project.</para>
/// </summary>
[Trait("Category", LinuxTentacleE2ECategories.TentacleBinary)]
public sealed class StubSquidServerSmokeE2ETests
{
    // ========================================================================
    // StubSquidServer_Start_ExposesUrisAndThumbprint
    //
    // The smallest viable smoke: start the stub, verify it returns
    // working URIs (polling + REST) + a 40-char hex thumbprint, then
    // dispose cleanly. Catches:
    //   - Cert generation failure on Linux (CryptographicException)
    //   - HttpListener bind failure (port allocation race)
    //   - HalibutRuntime startup failure (Halibut Linux compatibility)
    //   - DisposeAsync hang or throw
    //
    // Intentionally does NOT register an agent or dispatch a script —
    // those paths are exercised by full Linux deploy E2E tests in
    // follow-up PRs.
    // ========================================================================

    [Fact]
    public async Task StubSquidServer_Start_ExposesUrisAndThumbprint()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var server = await StubSquidServer.StartAsync();

        // Polling URI: Halibut's listener URI for polling agents to dial.
        server.PollingUri.ShouldNotBeNull(
            customMessage: "StubSquidServer.PollingUri MUST be set after StartAsync — Halibut polling listener bind failed?");
        server.PollingUri.Scheme.ShouldBe("https",
            customMessage: $"polling URI scheme MUST be https for Halibut TLS handshake. Got: {server.PollingUri.Scheme}.");
        server.PollingUri.Port.ShouldBeGreaterThan(0,
            customMessage: $"polling URI port MUST be > 0 (got {server.PollingUri.Port}). " +
                          "Random port allocation via TcpListener(0) failed?");

        // Server URI: REST listener for register handshake.
        server.ServerUri.ShouldNotBeNull(
            customMessage: "ServerUri MUST be set after StartAsync — HttpListener bind failed?");
        server.ServerUri.Scheme.ShouldBe("http",
            customMessage: $"server URI scheme MUST be http (HttpListener doesn't bind https without netsh setup). Got: {server.ServerUri.Scheme}.");

        // Server thumbprint: 40-char SHA-1 hex (X.509 thumbprint format).
        server.ServerThumbprint.ShouldNotBeNullOrWhiteSpace(
            customMessage: "ServerThumbprint MUST be set after cert generation — X509Certificate2 generation failed?");
        server.ServerThumbprint.Length.ShouldBe(40,
            customMessage: $"ServerThumbprint MUST be 40-char SHA-1 hex. Got '{server.ServerThumbprint}' (length {server.ServerThumbprint.Length}).");

        // No registrations yet — pre-condition for downstream tests.
        server.ReceivedRegistrations.Count.ShouldBe(0,
            customMessage: $"ReceivedRegistrations MUST start empty. Got {server.ReceivedRegistrations.Count}.");

        // DisposeAsync (implicit via `await using`) must complete cleanly
        // — if it throws, downstream tests can't reliably tear down.
    }
}
