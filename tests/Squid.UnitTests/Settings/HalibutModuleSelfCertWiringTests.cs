using System;
using System.IO;
using System.Text.Json;
using Autofac;
using Halibut;
using Shouldly;
using Squid.Core.Halibut;
using Squid.Core.Settings.SelfCert;
using Squid.UnitTests.Support;
using Xunit;

namespace Squid.UnitTests.Settings;

/// <summary>
/// Pins that <see cref="HalibutModule"/> actually CALLS the SelfCert guard.
///
/// <para><b>Why this exists separately from <c>SelfCertValidatorTests</c></b>: those exercise the
/// validator in isolation, which proves it makes the right decision but nothing about whether it
/// is on the path that serves the identity. Deleting both call sites from <c>HalibutModule</c>
/// left the entire suite green — the validator would still be perfect and production still
/// unprotected. The bug class this whole guard exists to prevent is a seam nobody tested across,
/// so the seam itself is pinned here.</para>
///
/// <para>These resolve the real registration. The guard runs before any Halibut runtime or
/// listener is constructed, so a rejected identity fails without opening a socket.</para>
/// </summary>
[Collection(GlobalStateSerialisedCollection.Name)]
public sealed class HalibutModuleSelfCertWiringTests
{
    [Fact]
    public void ResolvingHalibutRuntime_WithTheCommittedIdentity_IsRejected()
    {
        // Pins WIRING, not the default, so the mode is set explicitly — leaving it ambient would
        // make this test go red on a developer machine that exported the documented dev opt-out.
        var restore = SetEnforcementEnvVar("strict");

        try
        {
            var committed = ReadCommittedSelfCert();

            using var container = BuildContainer(committed.Base64, committed.Password);

            var ex = Should.Throw<Exception>(() => container.Resolve<HalibutRuntime>());
            var message = Unwrap(ex).Message;

            // NOT the env-var name: EnsureConfigured's message names it too, so asserting on it
            // would pass just as happily if the identity were missing entirely and the published-
            // identity check had been deleted. The thumbprint appears in only one of the two.
            message.ShouldContain(CommittedThumbprint(),
                customMessage: "Resolving the runtime with the committed identity must surface the published-" +
                               "identity rejection, which names the offending thumbprint. A different failure " +
                               "means HalibutModule is no longer calling EnsureNotPublishedIdentity.");
        }
        finally
        {
            restore();
        }
    }

    [Fact]
    public void ResolvingHalibutRuntime_WithNoIdentityConfigured_FailsWithAnActionableMessage()
    {
        // Unconditional by design — no mode substitutes for an absent identity — so this one is
        // genuinely mode-independent and needs no env pinning.
        using var container = BuildContainer(base64: string.Empty, password: string.Empty);

        var ex = Should.Throw<Exception>(() => container.Resolve<HalibutRuntime>());
        var message = Unwrap(ex).Message;

        message.ShouldContain(SelfCertValidator.SettingPath,
            customMessage: "An absent identity must name the setting to populate, not surface an opaque " +
                           "FormatException from inside the PKCS#12 loader. A different message means " +
                           "HalibutModule is no longer calling EnsureConfigured.");
    }

    [Fact]
    public void ResolvingHalibutRuntime_WithADeploymentSpecificIdentity_Succeeds()
    {
        // Guards against 'fix by rejecting everything': a properly configured deployment must
        // still get a runtime. Also proves the two tests above fail for the RIGHT reason rather
        // than because resolution is broken in this harness.
        var restore = SetEnforcementEnvVar("strict");

        try
        {
            var (base64, password) = CreateDeploymentSpecificPkcs12();

            using var container = BuildContainer(base64, password);

            var runtime = container.Resolve<HalibutRuntime>();

            runtime.ShouldNotBeNull();
        }
        finally
        {
            restore();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Autofac unwraps registration failures in DependencyResolutionException.</summary>
    private static Exception Unwrap(Exception ex)
    {
        while (ex.InnerException != null) ex = ex.InnerException;

        return ex;
    }

    private static IContainer BuildContainer(string base64, string password)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new SelfCertSetting { Base64 = base64, Password = password }).AsSelf().SingleInstance();
        builder.RegisterModule(new HalibutModule());

        return builder.Build();
    }

    private static Action SetEnforcementEnvVar(string value)
    {
        var name = SelfCertValidator.EnforcementEnvVar;
        var prior = Environment.GetEnvironmentVariable(name);

        Environment.SetEnvironmentVariable(name, value);

        return () => Environment.SetEnvironmentVariable(name, prior);
    }

    private static (string Base64, string Password) ReadCommittedSelfCert()
    {
        var path = Path.Combine(RepoRoot(), "src", "Squid.Api", "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var selfCert = doc.RootElement.GetProperty("SelfCert");
        var base64 = selfCert.GetProperty("Base64").GetString();

        // Without this, a future appsettings.json that drops SelfCert:Base64 would silently turn
        // the rejection test into an EnsureConfigured test that still passes for the wrong reason.
        base64.ShouldNotBeNullOrWhiteSpace("the committed appsettings.json is expected to still carry a SelfCert");

        return (base64, selfCert.GetProperty("Password").GetString());
    }

    /// <summary>Recomputed, not hardcoded, so it tracks a rotated committed certificate.</summary>
    private static string CommittedThumbprint()
    {
        var committed = ReadCommittedSelfCert();
        using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader
            .LoadPkcs12(Convert.FromBase64String(committed.Base64), committed.Password);

        return cert.Thumbprint;
    }

    private static (string Base64, string Password) CreateDeploymentSpecificPkcs12()
    {
        const string password = "test-password";

        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=deployment-specific", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        var now = DateTimeOffset.UtcNow;
        using var cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));

        var bytes = cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, password);

        return (Convert.ToBase64String(bytes), password);
    }

    /// <summary>
    /// Walks up to the repository root. Accepts <c>.git</c> as either a directory (normal clone)
    /// or a file (linked worktree, which is how review/agent tooling checks the branch out) —
    /// a directory-only probe walks straight past a worktree root and fails spuriously there.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate .git — test must run inside the Squid repo working tree");
    }
}
