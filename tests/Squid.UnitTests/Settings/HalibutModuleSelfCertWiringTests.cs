using System;
using System.IO;
using System.Text.Json;
using Autofac;
using Halibut;
using Shouldly;
using Squid.Core.Halibut;
using Squid.Core.Settings.SelfCert;
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
public sealed class HalibutModuleSelfCertWiringTests
{
    [Fact]
    public void ResolvingHalibutRuntime_WithTheCommittedIdentity_IsRejected()
    {
        // The end-to-end statement: with the repository's own certificate configured and no
        // enforcement env var set, the server must refuse to build its Halibut identity.
        var committed = ReadCommittedSelfCert();

        using var container = BuildContainer(committed.Base64, committed.Password);

        var ex = Should.Throw<Exception>(() => container.Resolve<HalibutRuntime>());
        var message = Unwrap(ex).Message;

        message.ShouldContain(SelfCertValidator.EnforcementEnvVar,
            customMessage: "Resolving the runtime with the committed identity must surface the SelfCert guard's " +
                           "rejection (which names its escape hatch). A different failure means HalibutModule is " +
                           "no longer calling EnsureNotPublishedIdentity.");
    }

    [Fact]
    public void ResolvingHalibutRuntime_WithNoIdentityConfigured_FailsWithAnActionableMessage()
    {
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
        var (base64, password) = CreateDeploymentSpecificPkcs12();

        using var container = BuildContainer(base64, password);

        var runtime = container.Resolve<HalibutRuntime>();

        runtime.ShouldNotBeNull();
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

    private static (string Base64, string Password) ReadCommittedSelfCert()
    {
        var path = Path.Combine(RepoRoot(), "src", "Squid.Api", "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var selfCert = doc.RootElement.GetProperty("SelfCert");

        return (selfCert.GetProperty("Base64").GetString(), selfCert.GetProperty("Password").GetString());
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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate .git — test must run inside the Squid repo working tree");
    }
}
