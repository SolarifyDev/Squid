using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Serilog;

namespace Squid.Tentacle.Certificate;

/// <summary>
/// Builds TLS certificate validation callbacks for Tentacle → Server HTTP connections.
/// Supports thumbprint pinning (for self-signed server certs) and standard chain validation.
/// </summary>
public static class ServerCertificateValidator
{
    /// <summary>
    /// Creates a <see cref="HttpClientHandler.ServerCertificateCustomValidationCallback"/>
    /// that validates the server certificate using the following strategy:
    /// <list type="number">
    ///   <item>If the OS certificate chain validates cleanly, accept immediately.</item>
    ///   <item>If <paramref name="expectedThumbprint"/> is configured and matches, accept (thumbprint pinning).</item>
    ///   <item>If <paramref name="expectedThumbprint"/> is configured but mismatches, reject.</item>
    ///   <item>If no thumbprint is configured and chain validation failed, accept with a warning
    ///         (backward-compatible — operators should configure <c>ServerCertificate</c>).</item>
    /// </list>
    /// </summary>
    public static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>
        Create(string? expectedThumbprint)
    {
        return Create(ParseThumbprints(expectedThumbprint));
    }

    /// <summary>
    /// Multi-thumbprint overload — mirrors Octopus's <c>TrustedOctopusServers</c>
    /// list. Useful when rotating Server certs (trust both old and new while
    /// deploying the rotation) or when a Tentacle talks to multiple Servers
    /// (each with its own self-signed cert).
    /// </summary>
    public static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>
        Create(IReadOnlyCollection<string> expectedThumbprints)
    {
        var trusted = (expectedThumbprints ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (_, cert, _, sslPolicyErrors) =>
        {
            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;

            if (trusted.Count > 0)
            {
                var actual = cert?.Thumbprint;

                if (actual != null && trusted.Contains(actual))
                    return true;

                Log.Warning(
                    "Server certificate thumbprint mismatch: expected one of [{Expected}], got {Actual}. " +
                    "Verify the ServerCertificate setting matches one of the Squid Server's certificates",
                    string.Join(", ", trusted), actual ?? "(null)");
                return false;
            }

            // Backward-compatible: accept but warn so operators know to configure pinning
            Log.Warning(
                "Server TLS certificate has validation errors ({Errors}) and no ServerCertificate " +
                "thumbprint is configured for pinning — accepting for backward compatibility. " +
                "Set the 'ServerCertificate' setting to the server's certificate thumbprint to enable pinning",
                sslPolicyErrors);
            return true;
        };
    }

    /// <summary>
    /// Parses a comma-separated thumbprint string into a clean list. Accepts:
    /// <c>"FAF04764"</c>, <c>"FAF04764,6EE6575D"</c>, or surrounding whitespace.
    /// </summary>
    public static IReadOnlyList<string> ParseThumbprints(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
