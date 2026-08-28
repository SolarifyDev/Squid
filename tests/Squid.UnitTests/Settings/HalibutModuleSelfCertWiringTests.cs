using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autofac;
using Halibut;
using Serilog;
using Serilog.Events;
using Shouldly;
using Squid.Core.Halibut;
using Squid.Core.Settings.SelfCert;
using Squid.UnitTests.Support;
using Xunit;

namespace Squid.UnitTests.Settings;

/// <summary>
/// Pins that <see cref="HalibutModule"/> actually WIRES the SelfCert guard, and where.
///
/// <para><b>Why this exists separately from <c>SelfCertValidatorTests</c></b>: those exercise the
/// validator in isolation, which proves it makes the right decision but nothing about whether it
/// is on a path that runs. The 1.9.5 incident was a WIRING failure mode: enforcement lived only
/// inside the lazily-resolved <c>HalibutRuntime</c> factory, the one startup consumer swallowed
/// its exception into a log line, and the strict-mode server came up half-alive — web UI working,
/// every deploy and health check failing with an Autofac resolution exception.</para>
///
/// <para>The contract pinned here: enforcement runs synchronously at container BUILD via
/// <see cref="SelfCertStartupCheck"/>. Strict refuses to start, cleanly; the default (Warn) logs
/// the rotation warning once at startup and the server works; a missing or unloadable identity
/// never fails the build — those keep failing lazily in the factory with actionable context.</para>
/// </summary>
[Collection(GlobalStateSerialisedCollection.Name)]
public sealed class HalibutModuleSelfCertWiringTests
{
    [Fact]
    public void StrictMode_CommittedIdentity_FailsContainerBuild()
    {
        // Fail-fast is the point of opting into strict: the server must refuse to START, not
        // come up half-alive and fail every deploy at DI resolution.
        var restore = SetEnforcementEnvVar("strict");

        try
        {
            var committed = ReadCommittedSelfCert();

            var ex = Should.Throw<Exception>(() => BuildContainer(committed.Base64, committed.Password));
            var message = Unwrap(ex).Message;

            message.ShouldContain(CommittedThumbprint(),
                customMessage: "Container build with the committed identity under strict must surface the " +
                               "published-identity rejection, which names the offending thumbprint. A different " +
                               "failure means SelfCertStartupCheck is no longer registered or no longer checks.");
        }
        finally
        {
            restore();
        }
    }

    [Fact]
    public void DefaultMode_CommittedIdentity_BuildsRunsAndWarnsOnce()
    {
        // The non-breaking half of the contract: an un-rotated deployment upgrading in place
        // must keep working — container builds, Halibut resolves — and the operator must be
        // told at startup, where they actually look, exactly what to rotate.
        var restoreEnv = SetEnforcementEnvVar(null);
        var (sink, restoreLogger) = InstallCapturingLogger();

        try
        {
            var committed = ReadCommittedSelfCert();

            using var container = BuildContainer(committed.Base64, committed.Password);
            var runtime = container.Resolve<HalibutRuntime>();

            runtime.ShouldNotBeNull("an un-rotated identity must still produce a working runtime under the default mode");

            var warnings = sink.Events.Where(e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains(CommittedThumbprint())).ToList();

            warnings.Count.ShouldBe(1,
                customMessage: "The startup check must warn about the published identity exactly once. Zero means " +
                               "the guard is silently dead; more than one means enforcement is duplicated again.");
        }
        finally
        {
            restoreLogger();
            restoreEnv();
        }
    }

    [Fact]
    public void NoIdentityConfigured_DoesNotFailBuild_AndResolvingStillFailsActionably()
    {
        // The startup check must never turn a server that used to start into one that does not:
        // a missing identity keeps today's lazy behaviour — build succeeds, first Halibut use
        // fails with the message that names the setting to populate.
        using var container = BuildContainer(base64: string.Empty, password: string.Empty);

        var ex = Should.Throw<Exception>(() => container.Resolve<HalibutRuntime>());

        Unwrap(ex).Message.ShouldContain(SelfCertValidator.SettingPath,
            customMessage: "An absent identity must name the setting to populate, not surface an opaque " +
                           "FormatException from inside the PKCS#12 loader. A different message means " +
                           "HalibutModule is no longer calling EnsureConfigured.");
    }

    [Fact]
    public void UnloadableIdentity_DoesNotFailBuild()
    {
        // Corrupt Base64 is the loader's problem at first use, not the startup check's: turning
        // it into a build failure would be a new way for an upgrade to stop a server starting.
        var restore = SetEnforcementEnvVar("strict");

        try
        {
            using var container = BuildContainer(base64: "not-base64!!", password: "x");

            container.ShouldNotBeNull();
        }
        finally
        {
            restore();
        }
    }

    [Fact]
    public void StrictMode_DeploymentSpecificIdentity_BuildsAndResolves()
    {
        // Guards against 'fix by rejecting everything': a rotated deployment opting into strict
        // must build and get a runtime. Also proves the strict-rejection test above fails for
        // the RIGHT reason rather than because building is broken in this harness.
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

    /// <summary>Autofac wraps registration/startable failures in DependencyResolutionException.</summary>
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

    private static (CapturingLogSink Sink, Action Restore) InstallCapturingLogger()
    {
        var original = Log.Logger;
        var sink = new CapturingLogSink();

        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        return (sink, () => Log.Logger = original);
    }

    private sealed class CapturingLogSink : Serilog.Core.ILogEventSink
    {
        private readonly List<LogEvent> _events = new();

        public IReadOnlyList<LogEvent> Events { get { lock (_events) return _events.ToList(); } }

        public void Emit(LogEvent logEvent) { lock (_events) _events.Add(logEvent); }
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
