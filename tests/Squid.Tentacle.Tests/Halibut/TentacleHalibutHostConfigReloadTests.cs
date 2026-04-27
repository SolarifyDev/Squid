using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Halibut;
using Shouldly;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Core;
using Squid.Tentacle.Halibut;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Halibut;

/// <summary>
/// P1-Phase9b.4 — pin the SIGHUP-driven trust-list hot-reload contract.
///
/// <para><b>Why this exists</b>: pre-Phase-9b.4, rotating the server's TLS
/// certificate forced operators to restart every running Tentacle in the
/// fleet (the in-process trust list was loaded once at startup). Now
/// SIGHUP triggers a re-read of <c>ServerCertificate</c> + atomic
/// <see cref="HalibutRuntime.TrustOnly"/> — zero-downtime cert rotation.</para>
///
/// <para>Tests focus on <see cref="TentacleHalibutHost.ReloadTrustList"/>
/// directly. The signal-hook plumbing (<c>HookConfigReloadOnSighup</c>) is
/// platform-gated by .NET's <c>PosixSignalRegistration</c> and tested via
/// the cross-platform return-disposable behaviour, not by actually firing
/// SIGHUP (which is brittle in unit tests).</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class TentacleHalibutHostConfigReloadTests : IDisposable
{
    private readonly TentacleHalibutHost _host;
    private readonly HalibutRuntime _exposedRuntime;

    public TentacleHalibutHostConfigReloadTests()
    {
        var cert = GenerateSelfSignedCert();
        var settings = new TentacleSettings
        {
            ServerCommsUrl = "https://server.example.com:10943"
        };

        _host = new TentacleHalibutHost(
            cert,
            scriptService: new NoopScriptService(),
            settings,
            capabilitiesService: new CapabilitiesService());

        // Internal access to runtime via reflection — host doesn't expose it directly
        var runtimeField = typeof(TentacleHalibutHost).GetField("_runtime",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        _exposedRuntime = (HalibutRuntime)runtimeField!.GetValue(_host)!;
    }

    public void Dispose()
    {
        try { _host?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
    }

    // ── ReloadTrustList semantics ────────────────────────────────────────────

    [Fact]
    public void ReloadTrustList_NewThumbprint_TrustsNewAndRemovesOld()
    {
        // Initial: trust two thumbprints
        _host.ReloadTrustList("OLD-A,OLD-B");
        _exposedRuntime.IsTrusted("OLD-A").ShouldBeTrue();
        _exposedRuntime.IsTrusted("OLD-B").ShouldBeTrue();

        // Simulated cert rotation: switch to new set
        _host.ReloadTrustList("NEW-A");

        // OLD thumbprints must NO LONGER be trusted (TrustOnly replaces)
        _exposedRuntime.IsTrusted("OLD-A").ShouldBeFalse(customMessage:
            "OLD-A must be removed from trust after rotation — pre-Phase-9b.4 it would persist forever.");
        _exposedRuntime.IsTrusted("OLD-B").ShouldBeFalse();
        _exposedRuntime.IsTrusted("NEW-A").ShouldBeTrue();
    }

    [Fact]
    public void ReloadTrustList_MultiThumbprintList_AllTrusted()
    {
        _host.ReloadTrustList("THUMB-1,THUMB-2,THUMB-3");

        _exposedRuntime.IsTrusted("THUMB-1").ShouldBeTrue();
        _exposedRuntime.IsTrusted("THUMB-2").ShouldBeTrue();
        _exposedRuntime.IsTrusted("THUMB-3").ShouldBeTrue();
    }

    [Fact]
    public void ReloadTrustList_NullOrEmpty_DoesNotWipeTrust()
    {
        // Defensive: pre-fix list is "OLD-A". Operator misconfigures
        // ServerCertificate as empty by accident. We REFUSE to wipe
        // trust silently — restart is required to clear, log a warning.
        _host.ReloadTrustList("OLD-A");
        _exposedRuntime.IsTrusted("OLD-A").ShouldBeTrue();

        _host.ReloadTrustList(null);
        _exposedRuntime.IsTrusted("OLD-A").ShouldBeTrue(customMessage:
            "Null input must NOT wipe trust — that would silently break every polling agent.");

        _host.ReloadTrustList("");
        _exposedRuntime.IsTrusted("OLD-A").ShouldBeTrue();

        _host.ReloadTrustList("   ");
        _exposedRuntime.IsTrusted("OLD-A").ShouldBeTrue();
    }

    [Fact]
    public void ReloadTrustList_MalformedInput_LogsAndRetains()
    {
        // ParseThumbprints returns empty for a payload of only commas/whitespace.
        // We log + retain previous state.
        _host.ReloadTrustList("KEEP-ME");
        _host.ReloadTrustList(",,,");

        _exposedRuntime.IsTrusted("KEEP-ME").ShouldBeTrue(customMessage:
            "Malformed input ',,,' must NOT wipe trust list.");
    }

    // ── HookConfigReloadOnSighup — platform-portable plumbing ────────────────

    [Fact]
    public void HookConfigReloadOnSighup_NullCallback_Throws()
    {
        Should.Throw<ArgumentNullException>(() => _host.HookConfigReloadOnSighup(null));
    }

    [Fact]
    public void HookConfigReloadOnSighup_ReturnsDisposable_OnAllPlatforms()
    {
        // PosixSignalRegistration throws PlatformNotSupportedException on
        // platforms that don't support SIGHUP (e.g. Windows). The host
        // catches that and returns a no-op IDisposable so the caller's
        // `using` plumbing works identically across platforms.
        var registration = _host.HookConfigReloadOnSighup(() => "DOESNT-MATTER");

        registration.ShouldNotBeNull();
        Should.NotThrow(() => registration.Dispose());
    }

    private static X509Certificate2 GenerateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>Minimal IScriptService stub for host construction.</summary>
    private sealed class NoopScriptService : IScriptService
    {
        private static ScriptStatusResponse Empty()
            => new(new ScriptTicket("noop"), ProcessState.Complete, 0, new List<ProcessOutput>(), 0);

        public ScriptStatusResponse StartScript(StartScriptCommand command) => Empty();
        public ScriptStatusResponse GetStatus(ScriptStatusRequest request) => Empty();
        public ScriptStatusResponse CancelScript(CancelScriptCommand command) => Empty();
        public ScriptStatusResponse CompleteScript(CompleteScriptCommand command) => Empty();
    }
}
