using System.Security.Cryptography.X509Certificates;
using Squid.Message.Hardening;

namespace Squid.Core.Settings.SelfCert;

/// <summary>
/// Guards the Halibut server identity — the certificate whose thumbprint every Tentacle pins
/// for mTLS — against being served from the value committed to this repository.
///
/// <para><b>Why this exists</b>: <c>appsettings.json</c> ships a real, working PKCS#12 under
/// <c>SelfCert:Base64</c> with its password beside it. A deployment is expected to override both,
/// but nothing enforced that, so a deploy that simply forgot would silently serve the public
/// identity: anyone with repository read access could then impersonate the server to every agent
/// that trusts that thumbprint and dispatch scripts to the whole fleet. The
/// <c>Security:VariableEncryption:MasterKey</c> setting got exactly this class of guard; this is
/// the same treatment for a strictly more dangerous secret.</para>
///
/// <para><b>Behaviour matrix</b> (mirrors <c>VariableEncryptionService.ValidateMasterKey</c>):</para>
/// <list type="table">
///   <item><term>null / empty / whitespace Base64</term>
///         <description>ALWAYS throw — no mode helps, Halibut cannot start without an
///         identity, and the alternative is an opaque failure deep inside the loader.</description></item>
///   <item><term>a known-published thumbprint</term>
///         <description>Off → allow silently; Warn → allow + warn; Strict (default) → throw.</description></item>
///   <item><term>any other certificate</term>
///         <description>All modes allow silently.</description></item>
/// </list>
/// </summary>
public static class SelfCertValidator
{
    /// <summary>
    /// Operator escape hatch (Rule 8): selects how a known-published server identity is handled.
    /// Recognised values <c>off</c> / <c>warn</c> / <c>strict</c>; unset defaults to
    /// <see cref="EnforcementMode.Strict"/>.
    ///
    /// <para>Pinned literal — renaming it silently re-arms the rejection for every operator who
    /// set it by its documented name.</para>
    /// </summary>
    public const string EnforcementEnvVar = "SQUID_SELFCERT_ENFORCEMENT";

    public const string SettingPath = "SelfCert:Base64";

    /// <summary>
    /// Mode used when <see cref="EnforcementEnvVar"/> is unset. Deliberately stricter than the
    /// shared <see cref="EnforcementModeReader.Read(string, EnforcementMode)"/> default of
    /// <see cref="EnforcementMode.Warn"/>: a warning about a published server identity is easy to
    /// miss, and the consequence of missing it is fleet-wide impersonation. Same posture the
    /// master-key guard settled on.
    /// </summary>
    public const EnforcementMode DefaultMode = EnforcementMode.Strict;

    /// <summary>Resolves the configured mode, defaulting to <see cref="DefaultMode"/>.</summary>
    public static EnforcementMode ResolveMode() => EnforcementModeReader.Read(EnforcementEnvVar, DefaultMode);

    /// <summary>
    /// Thumbprints of server identities whose private key is public knowledge, so they can never
    /// be trusted again. A set rather than a single value: rotating the committed certificate
    /// must ADD the old thumbprint here, never replace it — an operator still running the
    /// previous one has to keep being told.
    ///
    /// <para><c>FAF0…AB33</c> is the <c>CN=squid-server</c> certificate committed to
    /// <c>appsettings.json</c>, whose PKCS#12 and password are both in the repository.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> KnownPublishedThumbprints =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FAF04764A5574B7DC939568818A8B8F1F168AB33" };

    /// <summary>
    /// Throws when <paramref name="rawBase64"/> cannot yield a server identity at all. Separated
    /// from the certificate check so the caller fails with an actionable message instead of a
    /// <see cref="FormatException"/> or <see cref="ArgumentNullException"/> from the loader.
    /// </summary>
    public static void EnsureConfigured(string rawBase64)
    {
        if (!string.IsNullOrWhiteSpace(rawBase64)) return;

        throw new InvalidOperationException(
            $"{SettingPath} is empty — the server has no Halibut identity and cannot accept agent connections. " +
            "Supply a base64-encoded PKCS#12 via deployment config / environment / secret store, together with " +
            "SelfCert:Password. This rejection is unconditional: no value of " + EnforcementEnvVar + " can " +
            "substitute for a missing identity.");
    }

    /// <summary>
    /// Rejects a server identity whose private key is published, according to
    /// <paramref name="mode"/>. Exposed <c>internal</c>-friendly (public static) so the unit suite
    /// can exercise the full (certificate × mode) matrix without building a DI container or a
    /// Halibut runtime.
    /// </summary>
    public static void EnsureNotPublishedIdentity(X509Certificate2 certificate, EnforcementMode mode)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        if (!KnownPublishedThumbprints.Contains(certificate.Thumbprint)) return;

        switch (mode)
        {
            case EnforcementMode.Off:
                return;

            case EnforcementMode.Warn:
                Log.Warning(
                    "Halibut server identity {Thumbprint} ({Subject}) is the certificate committed to the Squid " +
                    "repository — its private key and password are public. Anyone who can read the repository can " +
                    "impersonate this server to every agent that trusts this thumbprint. Replace {SettingPath} (and " +
                    "SelfCert:Password) with a deployment-specific certificate, then re-register or re-trust your " +
                    "agents. Set {EnvVar}=strict to refuse to serve it.",
                    certificate.Thumbprint, certificate.Subject, SettingPath, EnforcementEnvVar);
                return;

            case EnforcementMode.Strict:
                throw new InvalidOperationException(
                    $"Refusing to use Halibut server identity {certificate.Thumbprint} ({certificate.Subject}): it is " +
                    $"the certificate committed to the Squid repository, so its private key and password are public " +
                    $"knowledge. Anyone who can read the repository could impersonate this server to every agent that " +
                    $"trusts this thumbprint and run scripts across the fleet. Replace {SettingPath} and " +
                    $"SelfCert:Password with a deployment-specific certificate, then re-register or re-trust your " +
                    $"agents against the new thumbprint. To proceed anyway set {EnforcementEnvVar}=warn (allow + log) " +
                    $"or {EnforcementEnvVar}=off (silent) — only sensible for local development.");

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognised EnforcementMode");
        }
    }
}
