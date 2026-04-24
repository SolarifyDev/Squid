using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Serilog;
using Squid.Message.Hardening;

namespace Squid.Tentacle.Certificate;

/// <summary>
/// Builds TLS certificate validation callbacks for Tentacle → Server HTTP
/// connections. Supports thumbprint pinning (for self-signed server certs) and
/// standard chain validation.
///
/// <para>Follows the project-wide three-mode hardening pattern (CLAUDE.md
/// §"Hardening Three-Mode Enforcement"). Behaviour when no thumbprint is
/// configured AND chain validation fails depends on
/// <see cref="EnforcementEnvVar"/>: Off accepts silently, Warn (default) accepts
/// with warning (preserves backward compat for deploys that rely on a public CA
/// cert + don't pin a thumbprint), Strict rejects.</para>
/// </summary>
public static class ServerCertificateValidator
{
    /// <summary>
    /// Env var that selects the enforcement mode for unpinned-cert handling.
    /// Recognised values: <c>off</c> / <c>warn</c> / <c>strict</c>; default
    /// (unset / blank) is <see cref="EnforcementMode.Warn"/>.
    ///
    /// <para>Pinned literal; renaming breaks every operator who set the env
    /// var by its documented name.</para>
    /// </summary>
    public const string EnforcementEnvVar = "SQUID_SERVER_CERT_ENFORCEMENT";

    /// <summary>
    /// Creates a <see cref="HttpClientHandler.ServerCertificateCustomValidationCallback"/>
    /// validating the server certificate as follows:
    /// <list type="number">
    ///   <item>If the OS certificate chain validates cleanly, accept.</item>
    ///   <item>If <paramref name="expectedThumbprint"/> is configured and matches, accept.</item>
    ///   <item>If <paramref name="expectedThumbprint"/> is configured but mismatches, reject.</item>
    ///   <item>If no thumbprint is configured and chain validation failed,
    ///         delegate to the <see cref="EnforcementMode"/> resolved from
    ///         <see cref="EnforcementEnvVar"/>.</item>
    /// </list>
    /// </summary>
    public static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>
        Create(string? expectedThumbprint)
    {
        return Create(ParseThumbprints(expectedThumbprint));
    }

    /// <summary>
    /// Multi-thumbprint overload — mirrors Octopus's <c>TrustedOctopusServers</c>
    /// list. Useful when rotating Server certs or when a Tentacle talks to
    /// multiple Servers each with its own self-signed cert.
    /// </summary>
    public static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>
        Create(IReadOnlyCollection<string> expectedThumbprints)
    {
        var trusted = (expectedThumbprints ?? Array.Empty<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var mode = EnforcementModeReader.Read(EnforcementEnvVar);

        return (_, cert, _, sslPolicyErrors) =>
            ValidateCore(sslPolicyErrors, cert?.Thumbprint, trusted, mode);
    }

    /// <summary>
    /// Pure decision logic exposed for unit testing. Takes the inputs the
    /// callback would receive plus the resolved <see cref="EnforcementMode"/>
    /// and returns the accept/reject verdict. Tests exercise the full
    /// (chain-state × thumbprint-state × mode) matrix without staging a real
    /// TLS handshake.
    /// </summary>
    internal static bool ValidateCore(
        SslPolicyErrors sslPolicyErrors,
        string? actualThumbprint,
        IReadOnlySet<string> trusted,
        EnforcementMode mode)
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

        // No thumbprint configured + chain validation errors. Mode decides:
        // Off  → silent accept (dev / tests / explicit opt-out)
        // Warn → accept + warning (default — backward compat, MITM-door-open)
        // Strict → reject (production hardening)
        return EnforceUnpinned(sslPolicyErrors, mode);
    }

    private static bool EnforceUnpinned(SslPolicyErrors sslPolicyErrors, EnforcementMode mode)
    {
        switch (mode)
        {
            case EnforcementMode.Off:
                return true;

            case EnforcementMode.Warn:
                Log.Warning(
                    "Server TLS certificate has validation errors ({Errors}) and no ServerCertificate " +
                    "thumbprint is configured for pinning. Accepting in Warn mode — backward compat for " +
                    "deploys that rely on a public CA but didn't pin. THIS ALLOWS MITM ON THE SERVER " +
                    "CHANNEL. Set {EnvVar}=strict to refuse unpinned chain failures, or configure " +
                    "ServerCertificate with the Squid Server's thumbprint to fix at the source.",
                    sslPolicyErrors, EnforcementEnvVar);
                return true;

            case EnforcementMode.Strict:
                Log.Error(
                    "Server TLS certificate has validation errors ({Errors}) and no ServerCertificate " +
                    "thumbprint is configured for pinning. REJECTING the handshake (Strict mode) to " +
                    "prevent MITM. Fix: set the 'ServerCertificate' setting to the Squid Server's " +
                    "thumbprint, or set {EnvVar}=warn (allow + log) / {EnvVar}=off (silent) for " +
                    "non-production scenarios.",
                    sslPolicyErrors, EnforcementEnvVar);
                return false;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognised EnforcementMode");
        }
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
