using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Shouldly;
using Squid.Core.Settings.SelfCert;
using Squid.Message.Hardening;
using Xunit;

namespace Squid.UnitTests.Settings;

/// <summary>
/// Pins the guard that stops the repository's own Halibut server identity being served to a
/// production fleet.
///
/// <para><c>appsettings.json</c> ships a working PKCS#12 under <c>SelfCert:Base64</c> with its
/// password beside it. Deployments are expected to override both, but nothing enforced it, so a
/// deploy that forgot silently served the public identity — and anyone with repository read
/// access could impersonate the server to every agent pinning that thumbprint.</para>
/// </summary>
public sealed class SelfCertValidatorTests
{
    [Fact]
    public void EnforcementEnvVar_ConstantNamePinned()
    {
        // Rule 8: renaming this silently re-arms the rejection for every operator who set it by
        // its documented name — turning a working deploy into a hard startup failure.
        SelfCertValidator.EnforcementEnvVar.ShouldBe("SQUID_SELFCERT_ENFORCEMENT");
    }

    [Fact]
    public void DefaultMode_IsStrict_NotTheSharedWarnDefault()
    {
        // The shared EnforcementModeReader default is Warn. A warning about a published server
        // identity is easy to miss and the consequence is fleet-wide impersonation, so this guard
        // deliberately opts into the stricter posture — same call the master-key guard made.
        SelfCertValidator.DefaultMode.ShouldBe(EnforcementMode.Strict);
        SelfCertValidator.ResolveMode().ShouldBe(EnforcementMode.Strict,
            customMessage: "With the env var unset the guard must resolve Strict, not the shared Warn default.");
    }

    // ── The committed certificate ────────────────────────────────────────

    [Fact]
    public void KnownPublishedThumbprints_MatchesTheCertificateActuallyCommitted()
    {
        // Drift guard: the pinned thumbprint is a hand-copied constant, so it can silently stop
        // matching the file it describes — leaving the guard permanently inert while still
        // looking correct. Recompute it from the real appsettings.json instead of trusting it.
        var committed = LoadCommittedCertificate();

        SelfCertValidator.KnownPublishedThumbprints.ShouldContain(committed.Thumbprint,
            customMessage: $"The certificate committed to appsettings.json has thumbprint {committed.Thumbprint}, " +
                           "which is NOT in KnownPublishedThumbprints — the guard would let it through. If the " +
                           "committed certificate was rotated, ADD the new thumbprint (keep the old one so operators " +
                           "still running it are told).");
    }

    [Fact]
    public void CommittedCertificate_IsRejectedUnderTheDefaultMode()
    {
        // The end-to-end statement of intent: with no configuration at all, the identity in the
        // repository must not be usable.
        var committed = LoadCommittedCertificate();

        var ex = Should.Throw<InvalidOperationException>(
            () => SelfCertValidator.EnsureNotPublishedIdentity(committed, SelfCertValidator.ResolveMode()));

        ex.Message.ShouldContain(committed.Thumbprint);
        ex.Message.ShouldContain(SelfCertValidator.EnforcementEnvVar,
            customMessage: "The rejection must name its own escape hatch, or an operator who needs to proceed is stuck.");
    }

    [Theory]
    [InlineData(EnforcementMode.Off)]
    [InlineData(EnforcementMode.Warn)]
    public void CommittedCertificate_IsAllowedUnderTheEscapeHatchModes(EnforcementMode mode)
    {
        var committed = LoadCommittedCertificate();

        Should.NotThrow(() => SelfCertValidator.EnsureNotPublishedIdentity(committed, mode),
            customMessage: $"{mode} is the documented opt-out; local development depends on it.");
    }

    [Theory]
    [InlineData(EnforcementMode.Off)]
    [InlineData(EnforcementMode.Warn)]
    [InlineData(EnforcementMode.Strict)]
    public void ADeploymentSpecificCertificate_IsAllowedInEveryMode(EnforcementMode mode)
    {
        // Guards against 'fix by rejecting everything': the guard must be inert for the
        // overwhelmingly common case of a properly-configured deployment.
        using var ownCert = CreateSelfSignedCertificate();

        Should.NotThrow(() => SelfCertValidator.EnsureNotPublishedIdentity(ownCert, mode));
    }

    // ── Missing configuration ────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureConfigured_MissingBase64_ThrowsRegardlessOfMode(string rawBase64)
    {
        // Unconditional by design: no mode can substitute for an absent identity, and failing
        // here beats an opaque FormatException from deep inside the PKCS#12 loader.
        var ex = Should.Throw<InvalidOperationException>(() => SelfCertValidator.EnsureConfigured(rawBase64));

        ex.Message.ShouldContain(SelfCertValidator.SettingPath,
            customMessage: "The message must name the setting the operator has to populate.");
    }

    [Fact]
    public void EnsureConfigured_PresentBase64_DoesNotThrow()
    {
        Should.NotThrow(() => SelfCertValidator.EnsureConfigured("AAEC"));
    }

    [Fact]
    public void EnsureNotPublishedIdentity_NullCertificate_Throws()
    {
        Should.Throw<ArgumentNullException>(() => SelfCertValidator.EnsureNotPublishedIdentity(null, EnforcementMode.Strict));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Loads the certificate exactly as HalibutModule would, from the real appsettings.json.</summary>
    private static X509Certificate2 LoadCommittedCertificate()
    {
        var path = Path.Combine(RepoRoot(), "src", "Squid.Api", "appsettings.json");
        File.Exists(path).ShouldBeTrue($"expected appsettings.json at {path}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var selfCert = doc.RootElement.GetProperty("SelfCert");
        var base64 = selfCert.GetProperty("Base64").GetString();
        var password = selfCert.GetProperty("Password").GetString();

        base64.ShouldNotBeNullOrWhiteSpace("the committed appsettings.json is expected to still carry a SelfCert");

        return X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(base64), password);
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=deployment-specific", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;

        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate .git — test must run inside the Squid repo working tree");
    }
}
