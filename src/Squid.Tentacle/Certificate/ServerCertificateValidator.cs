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
    /// Env-var escape hatch: when set to <c>1</c>, <c>true</c>, or <c>yes</c>
    /// (case-insensitive), preserves the pre-1.6.x "accept any server cert
    /// with a warning if no thumbprint is configured" behaviour. Default
    /// (env var unset) is fail-closed: a tentacle with no configured
    /// <c>ServerCertificate</c> rejects invalid chains — this is the
    /// correct MITM-defeating default.
    ///
    /// <para>Opt in ONLY for lab / CI / air-gapped environments where
    /// the operator has knowingly accepted the risk. Pinning should be
    /// the norm in production.</para>
    ///
    /// <para>Pinned literal; renaming breaks every operator who set the
    /// env var by its documented name.</para>
    /// </summary>
    public const string AllowUnpinnedEnvVar = "SQUID_ALLOW_UNPINNED_SERVER_CERT";

    /// <summary>
    /// Creates a <see cref="HttpClientHandler.ServerCertificateCustomValidationCallback"/>
    /// that validates the server certificate using the following strategy:
    /// <list type="number">
    ///   <item>If the OS certificate chain validates cleanly, accept immediately.</item>
    ///   <item>If <paramref name="expectedThumbprint"/> is configured and matches, accept (thumbprint pinning).</item>
    ///   <item>If <paramref name="expectedThumbprint"/> is configured but mismatches, reject.</item>
    ///   <item>If no thumbprint is configured and chain validation failed, reject by default
    ///         (fail-closed) — unless the operator has opted in via
    ///         <see cref="AllowUnpinnedEnvVar"/>, in which case accept with a warning.</item>
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

        var allowUnpinned = ReadAllowUnpinned();

        return (_, cert, _, sslPolicyErrors) =>
            ValidateCore(sslPolicyErrors, cert?.Thumbprint, trusted, allowUnpinned);
    }

    /// <summary>
    /// Pure decision logic extracted for unit testing. Takes the inputs
    /// the callback would receive plus the opt-in flag and returns the
    /// accept/reject verdict. Exposed <c>internal static</c> so the full
    /// decision matrix (chain-valid, thumbprint-match, thumbprint-miss,
    /// no-thumbprint-no-optin, no-thumbprint-optin) can be tested without
    /// staging a real TLS handshake.
    /// </summary>
    internal static bool ValidateCore(
        SslPolicyErrors sslPolicyErrors,
        string? actualThumbprint,
        IReadOnlySet<string> trusted,
        bool allowUnpinned)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        if (trusted.Count > 0)
        {
            if (actualThumbprint != null && trusted.Contains(actualThumbprint))
                return true;

            Log.Warning(
                "Server certificate thumbprint mismatch: expected one of [{Expected}], got {Actual}. " +
                "Verify the ServerCertificate setting matches one of the Squid Server's certificates",
                string.Join(", ", trusted), actualThumbprint ?? "(null)");
            return false;
        }

        // No thumbprint configured + chain validation errors.
        // Pre-1.6.x: accepted with warning (MITM-door-wide-open).
        // Post-fix: fail-closed unless explicit opt-in via env var.
        if (allowUnpinned)
        {
            Log.Warning(
                "Server TLS certificate has validation errors ({Errors}) and no ServerCertificate " +
                "thumbprint is configured for pinning — accepting because {EnvVar}=1 is set. " +
                "This allows MITM attacks on the server channel. Configure ServerCertificate with " +
                "the Squid Server's thumbprint to eliminate the risk.",
                sslPolicyErrors, AllowUnpinnedEnvVar);
            return true;
        }

        Log.Error(
            "Server TLS certificate has validation errors ({Errors}) and no ServerCertificate " +
            "thumbprint is configured for pinning — REJECTING the handshake to prevent MITM. " +
            "Fix: set the 'ServerCertificate' setting to the Squid Server's certificate " +
            "thumbprint. For dev / lab / air-gapped scenarios where you knowingly accept the " +
            "risk of no pinning, set {EnvVar}=1 to allow unpinned connections.",
            sslPolicyErrors, AllowUnpinnedEnvVar);
        return false;
    }

    private static bool ReadAllowUnpinned()
    {
        var raw = Environment.GetEnvironmentVariable(AllowUnpinnedEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return false;

        var normalized = raw.Trim().ToLowerInvariant();

        return normalized == "1" || normalized == "true" || normalized == "yes";
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
