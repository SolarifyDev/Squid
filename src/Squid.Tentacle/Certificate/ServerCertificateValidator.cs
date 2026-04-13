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
        return (_, cert, _, sslPolicyErrors) =>
        {
            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;

            if (!string.IsNullOrWhiteSpace(expectedThumbprint))
            {
                var actual = cert?.Thumbprint;
                if (string.Equals(actual, expectedThumbprint, StringComparison.OrdinalIgnoreCase))
                    return true;

                Log.Warning(
                    "Server certificate thumbprint mismatch: expected {Expected}, got {Actual}. " +
                    "Verify the ServerCertificate setting matches the Squid Server's certificate",
                    expectedThumbprint, actual ?? "(null)");
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
}
