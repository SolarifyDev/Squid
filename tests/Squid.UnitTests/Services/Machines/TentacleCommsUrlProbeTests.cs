using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Squid.Core.Services.Machines.Scripts.Tentacle;

namespace Squid.UnitTests.Services.Machines;

/// <summary>
/// Behavioural coverage for <see cref="TentacleCommsUrlProbe"/>. The probe's job
/// is to turn operator-side network misconfigurations into actionable errors at
/// install-script-generation time rather than at first-Tentacle-handshake time.
///
/// <para>Listening-mode registration has no polling URL, so we only exercise
/// the Polling path here. The probe must distinguish three outcomes:</para>
/// <list type="bullet">
/// <item>Skipped — operator didn't supply a comms URL yet (blank)</item>
/// <item>Unreachable — DNS / TCP / TLS failed (each with its own remediation hint)</item>
/// <item>Reachable — TLS handshake succeeded, thumbprint may or may not match expected</item>
/// </list>
/// </summary>
public sealed class TentacleCommsUrlProbeTests : IDisposable
{
    private readonly TentacleCommsUrlProbe _probe = new();
    private readonly List<IDisposable> _resources = new();

    public void Dispose()
    {
        foreach (var d in _resources) d.Dispose();
    }

    [Fact]
    public async Task Probe_EmptyUrl_ReturnsSkipped()
    {
        var result = await _probe.ProbeAsync(commsUrl: "", expectedServerThumbprint: "AABB", CancellationToken.None);

        result.Skipped.ShouldBeTrue();
        result.Reachable.ShouldBeFalse();
        result.Detail.ShouldContain("not configured");
    }

    [Fact]
    public async Task Probe_MalformedUrl_ReturnsUnreachableWithRemediation()
    {
        var result = await _probe.ProbeAsync(commsUrl: "not-a-url", expectedServerThumbprint: "AABB", CancellationToken.None);

        result.Reachable.ShouldBeFalse();
        result.Skipped.ShouldBeFalse();
        result.Detail.ShouldContain("not a valid");
    }

    [Fact]
    public async Task Probe_UnreachableHost_ReturnsRemediationHintWithChecklist()
    {
        // Port 1 is almost certainly closed; TCP connect will fail fast.
        var result = await _probe.ProbeAsync(
            commsUrl: "https://127.0.0.1:1/",
            expectedServerThumbprint: "DEADBEEF",
            CancellationToken.None);

        result.Reachable.ShouldBeFalse();
        result.ObservedThumbprint.ShouldBeEmpty();
        // Hint must surface the most common root causes so operators know where to look
        // without digging through Seq — same signals we'd share in CLAUDE.md.
        result.Detail.ShouldContain("100.64.0.0/10");
        result.Detail.ShouldContain("TCP");
        result.Detail.ShouldContain("health check");
    }

    [Fact]
    public async Task Probe_ReachableTlsEndpointWithMatchingThumbprint_ReportsMatch()
    {
        using var cert = BuildSelfSignedCert();
        var expectedThumbprint = cert.Thumbprint; // uppercase hex by default in .NET
        var port = StartTlsEchoServer(cert);
        // Give the background accept loop a tick to be ready.
        await Task.Delay(50);

        var result = await _probe.ProbeAsync(
            commsUrl: $"https://127.0.0.1:{port}/",
            expectedServerThumbprint: expectedThumbprint,
            CancellationToken.None);

        result.Reachable.ShouldBeTrue(customMessage: $"Detail was: {result.Detail}");
        result.ThumbprintMatches.ShouldBeTrue();
        result.ObservedThumbprint.ShouldBe(expectedThumbprint, StringCompareShould.IgnoreCase);
        result.Detail.ShouldContain("matches");
    }

    [Fact]
    public async Task Probe_ReachableTlsEndpointWithWrongThumbprint_ReportsMismatch()
    {
        using var cert = BuildSelfSignedCert();
        var port = StartTlsEchoServer(cert);

        var result = await _probe.ProbeAsync(
            commsUrl: $"https://127.0.0.1:{port}/",
            expectedServerThumbprint: "BAADF00DBAADF00DBAADF00DBAADF00DBAADF00D",
            CancellationToken.None);

        result.Reachable.ShouldBeTrue();
        result.ThumbprintMatches.ShouldBeFalse();
        // Hint tells operator the typical cause: L7 proxy re-signing the cert
        result.Detail.ShouldContain("passthrough");
    }

    // ============================================================================
    // Test helpers
    // ============================================================================

    private static X509Certificate2 BuildSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=comms-probe-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        // Round-trip through PFX so the private key is retained across usage sites.
        var pfx = cert.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, password: null);
    }

    private int StartTlsEchoServer(X509Certificate2 cert)
    {
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        _resources.Add(new ListenerHandle(listener));

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = Task.Run(async () => await HandleClientAsync(client, cert).ConfigureAwait(false));
                }
            }
            catch (ObjectDisposedException) { /* listener stopped */ }
            catch (SocketException) { /* listener stopped */ }
        });

        return port;
    }

    // Handles one TCP connection: authenticates as TLS server, then keeps the
    // socket open briefly so the client can read the presented certificate
    // before we tear down (probe reads cert IMMEDIATELY after handshake, but a
    // too-quick close can race on some kernels and surface as a handshake
    // failure on the client side).
    private static async Task HandleClientAsync(TcpClient client, X509Certificate2 cert)
    {
        try
        {
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert,
                    ClientCertificateRequired = false
                }, CancellationToken.None).ConfigureAwait(false);

            // Keep the connection alive long enough for the probe to finish
            // reading RemoteCertificate on the client side.
            await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        }
        catch
        {
            // Expected: probe disconnects right after handshake.
        }
        finally
        {
            client.Dispose();
        }
    }

    private sealed class ListenerHandle(TcpListener listener) : IDisposable
    {
        public void Dispose()
        {
            try { listener.Stop(); } catch { /* best effort */ }
        }
    }
}
