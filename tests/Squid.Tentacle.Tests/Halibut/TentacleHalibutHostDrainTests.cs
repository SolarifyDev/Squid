using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Halibut;
using Squid.Tentacle.Tests.Support;

namespace Squid.Tentacle.Tests.Halibut;

/// <summary>
/// P0-T.4 regression guard (2026-04-24 audit).
///
/// <para>Pre-fix, <see cref="TentacleHalibutHost.StartPolling"/> threaded
/// <see cref="CancellationToken.None"/> into every <c>HalibutRuntime.Poll</c> call.
/// The only way to stop polling was disposing the runtime — which races with
/// in-flight RPCs and can drop responses. During graceful shutdown, the agent
/// kept accepting new work during the drain window and either extended drain
/// past its timeout or killed the new scripts mid-execution on final dispose.</para>
///
/// <para>Fix: the host owns a <see cref="CancellationTokenSource"/> whose token
/// is passed to every <c>Poll</c> call. <see cref="ITentacleHalibutHost.CancelPolling"/>
/// cancels that source; <c>TentacleApp.RunAsync</c> invokes it BEFORE the drain
/// wait so the script-backend gets a clean "no new work arriving" window.</para>
///
/// <para><b>Halibut contract boundary</b>: these tests prove the host cancels the
/// CT it owns when <c>CancelPolling</c> runs, and that the CT is the one wired
/// into <c>_runtime.Poll(uri, endpoint, token)</c>. Whether Halibut's runtime
/// actually observes that CT and exits its poll loop is Halibut's contract
/// (package version 8.1.1052). That contract is exercised end-to-end in
/// <c>Squid.E2ETests</c> by shutting down the real host mid-deploy and asserting
/// no new RPCs arrive during the drain window; it is NOT re-asserted here to
/// avoid unit-testing a third-party library. If Halibut changes its behaviour
/// around <c>Poll(ct)</c>, the E2E coverage catches it.</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class TentacleHalibutHostDrainTests
{
    [Fact]
    public async Task PollCancellationToken_FreshHost_NotCancelled()
    {
        await using var host = CreateHost();

        host.PollCancellationToken.IsCancellationRequested.ShouldBeFalse(
            customMessage: "a fresh host must not start with a cancelled token — that'd block Poll() instantly");
    }

    [Fact]
    public async Task CancelPolling_SignalsTheToken()
    {
        await using var host = CreateHost();

        host.CancelPolling();

        host.PollCancellationToken.IsCancellationRequested.ShouldBeTrue(
            customMessage:
                "CancelPolling() must cancel the token threaded into HalibutRuntime.Poll. If this " +
                "test fails, the poll loop keeps accepting new RPCs during drain — the P0-T.4 " +
                "regression is back.");
    }

    [Fact]
    public async Task CancelPolling_CalledTwice_NoThrow()
    {
        await using var host = CreateHost();

        host.CancelPolling();
        Should.NotThrow(
            () => host.CancelPolling(),
            customMessage:
                "idempotency contract. TentacleApp calls CancelPolling on shutdown, and DisposeAsync " +
                "calls it again — the second call must be safe.");
    }

    [Fact]
    public async Task CancelPolling_BeforeStartPolling_NoThrow()
    {
        // Listening-mode tentacles never call StartPolling. Shutdown still fires
        // CancelPolling through DisposeAsync — must be safe.
        await using var host = CreateHost();

        Should.NotThrow(() => host.CancelPolling());
    }

    [Fact]
    public async Task DisposeAsync_CancelsTokenImplicitly()
    {
        var host = CreateHost();
        var token = host.PollCancellationToken;

        await host.DisposeAsync();

        token.IsCancellationRequested.ShouldBeTrue(
            customMessage:
                "DisposeAsync must cancel polling before disposing the runtime — otherwise the " +
                "runtime-dispose races with in-flight Poll responses.");
    }

    // ── Helper: spin up a host without a real TLS listener / Halibut wire ───

    private static TentacleHalibutHost CreateHost()
    {
        var cert = CreateSelfSignedCertificate();
        var scriptService = new NoOpScriptService();
        var settings = new TentacleSettings();

        return new TentacleHalibutHost(cert, scriptService, settings);
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=squid-tentacle-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));

        // Export + reimport so the cert carries a private key from the CurrentUser
        // store-ish shape Halibut expects.
        var pfx = self.Export(X509ContentType.Pfx, "test");
#pragma warning disable SYSLIB0057 // legacy-style X509Certificate2(byte[], string, flags) still supported
        return new X509Certificate2(pfx, "test", X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
    }

    private sealed class NoOpScriptService : IScriptService
    {
        public ScriptStatusResponse StartScript(StartScriptCommand command) => null!;
        public ScriptStatusResponse GetStatus(ScriptStatusRequest request) => null!;
        public ScriptStatusResponse CancelScript(CancelScriptCommand command) => null!;
        public ScriptStatusResponse CompleteScript(CompleteScriptCommand command) => null!;
    }
}
