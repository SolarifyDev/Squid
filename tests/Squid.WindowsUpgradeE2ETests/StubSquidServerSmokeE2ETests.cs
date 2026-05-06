using System.Net.Sockets;
using Squid.WindowsUpgradeE2ETests.Infrastructure;

namespace Squid.WindowsUpgradeE2ETests;

/// <summary>
/// Phase 12.H smoke test for <see cref="StubSquidServer"/>. Establishes
/// that the fixture starts cleanly, exposes a usable thumbprint + polling
/// URI, accepts TCP connections on the bound port, and disposes
/// without leaking the port. Subsequent Phase 12.I+ tests assume these
/// invariants — a regression here pinpoints fixture vs test.
///
/// <para><b>Tier</b>: 🔵 Fixture-only (Rule 12 — does NOT count toward
/// production coverage). The stub itself is test infrastructure; these
/// tests just prove it works. Production E2E coverage starts in
/// Phase 12.I onward when real <c>Squid.Tentacle.exe</c> uses this stub.</para>
///
/// <para><b>Cross-platform</b>: Halibut runtime is .NET cross-platform, so
/// these tests run identically on macOS / Linux / Windows. No skip-on-OS
/// guard needed — that's a property of the production-binary tests
/// (Phase 12.I+), not of the stub itself.</para>
/// </summary>
[Trait("Category", WindowsUpgradeE2ECategories.StubSquidServer)]
public sealed class StubSquidServerSmokeE2ETests
{
    [Fact]
    public async Task StartAsync_ExposesThumbprintAndPollingUri()
    {
        await using var stub = await StubSquidServer.StartAsync();

        stub.ServerThumbprint.ShouldNotBeNullOrWhiteSpace(
            customMessage: "stub MUST expose its self-signed cert thumbprint so tests can pin it via `--thumbprint X` in the production tentacle CLI");

        // SHA-1 thumbprint is 40 hex chars.
        stub.ServerThumbprint.Length.ShouldBe(40,
            customMessage: $"thumbprint MUST be a 40-char SHA-1 hex string (got {stub.ServerThumbprint.Length} chars: '{stub.ServerThumbprint}')");

        stub.PollingUri.Scheme.ShouldBe("https",
            customMessage: "polling URI MUST be HTTPS — Halibut polling protocol requires TLS");

        stub.PollingUri.Host.ShouldBe("localhost",
            customMessage: "polling URI MUST be localhost — fixture must not expose to non-loopback");

        stub.PollingPort.ShouldBeGreaterThan(0,
            customMessage: "polling port MUST be a positive bound port (assigned by OS via TcpListener(0))");
    }

    [Fact]
    public async Task PollingPort_IsBoundAndAcceptingTcpConnections()
    {
        await using var stub = await StubSquidServer.StartAsync();

        // TCP-probe the polling port. Halibut should be listening on it. We
        // don't speak the Halibut TLS handshake here — that's tested by
        // Phase 12.I+ via real tentacle binary. Smoke check: the port is
        // bound and accepts SOMETHING. If TCP connect fails, Halibut.Listen
        // didn't bind correctly.
        using var client = new TcpClient();
        await client.ConnectAsync("localhost", stub.PollingPort);

        client.Connected.ShouldBeTrue(
            customMessage: $"TCP connect to bound polling port {stub.PollingPort} MUST succeed. If false, Halibut runtime didn't bind correctly to the listener.");
    }

    [Fact]
    public async Task DisposeAsync_ReleasesPollingPort()
    {
        // After dispose, the polling port MUST be released so a follow-up
        // test can rebind. Without this, parallel test classes would leak
        // ports and Halibut.Listen() would fail with "address in use".
        var stub = await StubSquidServer.StartAsync();
        var port = stub.PollingPort;

        await stub.DisposeAsync();

        // Wait a brief moment for the OS TCP stack to fully release the
        // port (close-wait state). 500ms is enough on modern OSes; any
        // longer wait suggests a Halibut shutdown bug.
        await Task.Delay(500);

        // Now try to rebind to the same port. If Halibut leaked it, this
        // throws SocketException "address already in use". We catch + fail
        // with a diagnostic rather than the bare exception.
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
        }
        catch (SocketException ex)
        {
            throw new Exception(
                $"after StubSquidServer.DisposeAsync(), port {port} could not be rebound: {ex.Message}. " +
                $"This means Halibut runtime didn't fully release the polling listener. " +
                $"Phase 12.I+ tests will accumulate leaked ports across runs.",
                ex);
        }
    }

    [Fact]
    public async Task TwoStubs_StartedConcurrently_GetDifferentPorts()
    {
        // Each test gets its own stub via Rule 12.2 (unique resources). Two
        // stubs running in parallel MUST get different ports — proves the
        // GetEphemeralPort helper actually queries the OS each time.
        await using var stub1 = await StubSquidServer.StartAsync();
        await using var stub2 = await StubSquidServer.StartAsync();

        stub1.PollingPort.ShouldNotBe(stub2.PollingPort,
            customMessage: $"two concurrent stubs MUST have different ports (got {stub1.PollingPort} for both). " +
                           $"If equal, GetEphemeralPort is caching or returning a hardcoded port — Phase 12.I+ parallel tests would deadlock.");
    }

    [Fact]
    public async Task TwoStubs_HaveDifferentThumbprints()
    {
        // Each stub gets its own self-signed cert. Cert subjects use a GUID
        // suffix → thumbprints differ. Tests pinning via --thumbprint X
        // need this so cross-test interference can't happen.
        await using var stub1 = await StubSquidServer.StartAsync();
        await using var stub2 = await StubSquidServer.StartAsync();

        stub1.ServerThumbprint.ShouldNotBe(stub2.ServerThumbprint,
            customMessage: "two stubs MUST have different cert thumbprints (cert subject uses unique GUID). " +
                           "If equal, certificate generation is caching — security test isolation is broken.");
    }
}
