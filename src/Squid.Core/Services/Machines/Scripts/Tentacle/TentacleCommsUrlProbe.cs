using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Serilog;

namespace Squid.Core.Services.Machines.Scripts.Tentacle;

/// <summary>
/// Probes a Tentacle polling URL end-to-end (DNS → TCP → TLS) to surface
/// operator-side network misconfigurations at install-script generation time
/// rather than at first-agent-handshake time.
///
/// <para>
/// History: the Squid cluster we operated hit a multi-hour regression where the
/// UI happily generated install scripts pointing at a polling URL that was
/// externally unreachable (SLB health check misconfigured → backend marked
/// unhealthy → all forwarded connections got RST before TLS even started).
/// The symptom surfaced only after a real agent tried to connect and saw
/// cryptic <c>"Received an unexpected EOF or 0 bytes from the transport stream"</c>
/// loops. This probe turns that silent failure into a loud warning embedded in
/// the script-generation response so operators fix their SLB / DNS / security
/// group before shipping the script to a new machine owner.
/// </para>
/// </summary>
public interface ITentacleCommsUrlProbe : IScopedDependency
{
    Task<TentacleCommsProbeResult> ProbeAsync(string commsUrl, string expectedServerThumbprint, CancellationToken ct);
}

public sealed class TentacleCommsUrlProbe : ITentacleCommsUrlProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TlsTimeout = TimeSpan.FromSeconds(5);

    public async Task<TentacleCommsProbeResult> ProbeAsync(string commsUrl, string expectedServerThumbprint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(commsUrl))
            return Skipped("ServerCommsUrl not configured — skipping probe");

        if (!Uri.TryCreate(commsUrl, UriKind.Absolute, out var uri))
            return Unreachable($"ServerCommsUrl \"{commsUrl}\" is not a valid absolute URL");

        var host = uri.Host;
        var port = uri.IsDefaultPort ? 10943 : uri.Port;

        return await ProbeHostAsync(host, port, expectedServerThumbprint, ct).ConfigureAwait(false);
    }

    private static async Task<TentacleCommsProbeResult> ProbeHostAsync(
        string host, int port, string expectedThumbprint, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, ct).AsTask()
                .WaitAsync(ConnectTimeout, ct).ConfigureAwait(false);

            // Only set the validation callback once (via SslClientAuthenticationOptions);
            // setting it both in the constructor AND in the options throws on .NET 9+.
            // The probe deliberately accepts any server cert — the probe's job is to
            // measure reachability and observe the thumbprint, not to enforce trust.
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);

            var tlsOptions = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            };

            await ssl.AuthenticateAsClientAsync(tlsOptions, ct)
                .WaitAsync(TlsTimeout, ct).ConfigureAwait(false);

            var observed = ExtractThumbprint(ssl.RemoteCertificate);
            var matches = !string.IsNullOrEmpty(expectedThumbprint)
                && string.Equals(observed, expectedThumbprint, StringComparison.OrdinalIgnoreCase);

            return new TentacleCommsProbeResult
            {
                Reachable = true,
                ObservedThumbprint = observed,
                ThumbprintMatches = matches,
                Detail = matches
                    ? $"Reachable. Server cert thumbprint matches ({expectedThumbprint})"
                    : $"Reachable but cert thumbprint mismatch. Expected {expectedThumbprint}, observed {observed}. "
                      + "Check that the SLB does L4 TCP passthrough (not TLS termination)"
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Tentacle comms URL probe failed for {Host}:{Port}", host, port);
            return Unreachable(BuildRemediationHint(ex, host, port));
        }
    }

    private static string BuildRemediationHint(Exception ex, string host, int port)
    {
        var cause = ex switch
        {
            TimeoutException => $"Connection to {host}:{port} timed out",
            SocketException se => $"TCP connect to {host}:{port} failed: {se.SocketErrorCode}",
            AuthenticationException ae => $"TLS handshake to {host}:{port} failed: {ae.Message}",
            IOException io when io.Message.Contains("EOF", StringComparison.OrdinalIgnoreCase)
                => $"SLB accepted TCP on {host}:{port} but dropped the TLS handshake (likely backend marked unhealthy)",
            _ => $"{ex.GetType().Name}: {ex.Message}"
        };

        return cause + ". Verify: "
            + "(1) DNS for " + host + " resolves to the SLB external IP; "
            + "(2) SLB listener for port " + port + " is protocol TCP (not HTTP); "
            + "(3) SLB health check is TCP (not HTTP GET); "
            + "(4) worker node security group allows 100.64.0.0/10 on NodePort range 30000-32767. "
            + "See CLAUDE.md > \"Kubernetes Deployment — Exposing Halibut Polling\".";
    }

    private static string ExtractThumbprint(X509Certificate cert)
    {
        if (cert == null) return string.Empty;
        if (cert is X509Certificate2 c2) return c2.GetCertHashString().ToUpperInvariant();

        using var loaded = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
        return loaded.GetCertHashString().ToUpperInvariant();
    }

    private static TentacleCommsProbeResult Skipped(string detail)
        => new() { Reachable = false, Skipped = true, Detail = detail };

    private static TentacleCommsProbeResult Unreachable(string detail)
        => new() { Reachable = false, Detail = detail };
}

public sealed class TentacleCommsProbeResult
{
    /// <summary>
    /// True once TCP + TLS both completed. False when the probe found a network
    /// gap (DNS / SLB / firewall / health check). Also false when skipped.
    /// </summary>
    public bool Reachable { get; set; }

    /// <summary>
    /// True when the probe was not performed (e.g. Listening-mode registration
    /// where there is no polling URL). Distinguishes "we deliberately didn't
    /// check" from "we checked and it's broken".
    /// </summary>
    public bool Skipped { get; set; }

    /// <summary>
    /// SHA1 thumbprint of the certificate the remote endpoint presented,
    /// uppercase hex. Empty when unreachable or skipped.
    /// </summary>
    public string ObservedThumbprint { get; set; } = string.Empty;

    /// <summary>
    /// True when <see cref="ObservedThumbprint"/> matches the server's expected
    /// Halibut cert thumbprint. False on mismatch — typically means an L7 proxy
    /// is terminating TLS and substituting its own cert, breaking Halibut mTLS.
    /// </summary>
    public bool ThumbprintMatches { get; set; }

    /// <summary>
    /// Human-readable diagnosis plus remediation hints; included in the install
    /// script response so operators can act without digging through server logs.
    /// </summary>
    public string Detail { get; set; } = string.Empty;
}
