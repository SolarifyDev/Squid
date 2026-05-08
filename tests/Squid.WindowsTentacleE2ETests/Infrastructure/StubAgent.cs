using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.ScriptExecution;

namespace Squid.WindowsTentacleE2ETests.Infrastructure;

// CROSS-PLATFORM SYNC NOTE: Linux project has a near-identical sibling
// at tests/Squid.LinuxTentacleE2ETests/Infrastructure/StubAgent.cs.
// Both wrap the production LocalScriptService behind Halibut. Bug fixes
// here MUST be mirrored there. Premature DRY is a real anti-pattern, so
// kept as parallel copies until either side diverges enough to justify
// the cross-project coupling cost of a shared infra project.

/// <summary>
/// In-process stub of a Squid Tentacle agent's Halibut surface — used by
/// Phase 12.J E2E tests that need the SERVER → AGENT script-dispatch
/// round-trip with REAL script execution (PowerShell on Windows, bash on
/// Linux / macOS) WITHOUT spawning the production <c>Squid.Tentacle.exe</c>
/// binary as a child process.
///
/// <para><b>Coverage delta vs <c>Squid.E2ETests.Infrastructure.TentacleStub</c>:</b>
/// The legacy TentacleStub uses a bash-only ScriptRunner so it can't drive
/// PowerShell on Windows. <see cref="StubAgent"/> wires the production
/// <see cref="LocalScriptService"/> directly — same script-execution path
/// the production tentacle uses, dispatching to PowerShell on Windows and
/// bash on Linux/macOS based on <see cref="StartScriptCommand.ScriptSyntax"/>.
/// Result: cross-OS deploy E2E without per-OS forks.</para>
///
/// <para><b>What it provides</b>:</para>
/// <list type="bullet">
///   <item>Self-signed agent certificate + thumbprint exposed to tests.</item>
///   <item>Listening mode: binds Halibut on a unique loopback port; server
///         (StubSquidServer) dials in via
///         <see cref="StubSquidServer.DispatchScriptToListeningAgentAsync"/>.</item>
///   <item>Polling mode: dials out to a server URL + subscription ID;
///         server queues commands via
///         <see cref="StubSquidServer.DispatchScriptToPollingAgentAsync"/>.</item>
///   <item>Trust helper so the agent accepts the server's cert during
///         the TLS handshake.</item>
/// </list>
///
/// <para><b>Tier</b>: Test infrastructure (🔵 fixture-only by Rule 12).
/// Tests that USE this stub achieve 🟢 high-fidelity because the path
/// from server-side dispatch through Halibut RPC to LocalScriptService
/// to real shell execution is all production code.</para>
///
/// <para><b>Lifetime</b>: <see cref="IAsyncDisposable"/>. Each test creates
/// its own instance; unique cert + port per stub means concurrent tests
/// don't collide.</para>
///
/// <para><b>Cross-platform</b>: Halibut + LocalScriptService are both
/// .NET-cross-platform. Tests using this stub run identically on Windows /
/// macOS / Linux — the OS-specific concern is the script syntax (caller
/// chooses PowerShell or Bash via the StartScriptCommand).</para>
/// </summary>
public sealed class StubAgent : IAsyncDisposable
{
    private readonly X509Certificate2 _agentCert;
    private readonly HalibutRuntime _runtime;
    private readonly LocalScriptService _scriptService;
    private readonly StubCapabilitiesService _capabilitiesService;
    private bool _disposed;

    /// <summary>SHA-1 thumbprint of the stub agent's self-signed cert.</summary>
    public string Thumbprint { get; }

    /// <summary>
    /// Agent version reported via the capabilities probe. Default is
    /// "1.6.0-stub". Tests that simulate post-upgrade state flip this to
    /// the new version mid-test, then re-probe to assert the fresh value
    /// reaches the server.
    /// </summary>
    public string AgentVersion
    {
        get => _capabilitiesService.AgentVersion;
        set => _capabilitiesService.AgentVersion = value;
    }

    /// <summary>
    /// Convenience setter for <see cref="AgentVersion"/>. Common pattern:
    /// probe → assert v1 → SetAgentVersion(v2) → probe → assert v2.
    /// </summary>
    public void SetAgentVersion(string version)
    {
        _capabilitiesService.AgentVersion = version;
    }

    /// <summary>
    /// The agent's Halibut listening URI (Listening mode only). Format:
    /// <c>https://localhost:&lt;loopback-port&gt;/</c>. Server uses this as
    /// the dial-out target.
    /// </summary>
    public Uri ListeningUri { get; }

    /// <summary>The Halibut listener port (Listening mode).</summary>
    public int ListeningPort { get; }

    /// <summary>The polling subscription ID (Polling mode only — null in Listening mode).</summary>
    public string SubscriptionId { get; }

    private StubAgent(X509Certificate2 cert, HalibutRuntime runtime, LocalScriptService scriptService, StubCapabilitiesService capabilitiesService, int listeningPort, string subscriptionId)
    {
        _agentCert = cert;
        _runtime = runtime;
        _scriptService = scriptService;
        _capabilitiesService = capabilitiesService;

        Thumbprint = cert.Thumbprint;
        ListeningPort = listeningPort;
        ListeningUri = listeningPort > 0 ? new Uri($"https://localhost:{listeningPort}/") : null;
        SubscriptionId = subscriptionId;
    }

    /// <summary>
    /// Starts a Listening-mode stub agent on a unique loopback port. The
    /// server dials in to <see cref="ListeningUri"/> to dispatch scripts.
    /// </summary>
    /// <param name="serverThumbprint">The server's cert thumbprint to
    /// trust. Without this, the TLS handshake from the server fails.</param>
    public static Task<StubAgent> StartListeningAsync(string serverThumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverThumbprint);

        var cert = CreateSelfSignedCert();
        var scriptService = new LocalScriptService();
        var capsService = new StubCapabilitiesService();
        var runtime = BuildRuntime(cert, scriptService, capsService);

        runtime.Trust(serverThumbprint);

        // Halibut.Listen returns the actually-bound port (caller suggestion
        // is just a hint). Re-read for the canonical URI (Rule 12.8).
        var assignedPort = runtime.Listen(GetEphemeralPort());

        return Task.FromResult(new StubAgent(cert, runtime, scriptService, capsService, assignedPort, subscriptionId: null));
    }

    /// <summary>
    /// Starts a Polling-mode stub agent that dials out to the given server
    /// polling URI. The agent connects with a unique subscription ID; the
    /// server queues commands for that subscription. Caller MUST trust the
    /// agent's <see cref="Thumbprint"/> on the server side BEFORE calling
    /// this method (otherwise the TLS handshake server-side rejects the
    /// agent's cert).
    /// </summary>
    /// <param name="serverPollingUri">e.g. <c>https://localhost:10943/</c>
    /// — the server's Halibut polling listener URI.</param>
    /// <param name="serverThumbprint">The server's cert thumbprint to trust.</param>
    public static Task<StubAgent> StartPollingAsync(Uri serverPollingUri, string serverThumbprint)
    {
        ArgumentNullException.ThrowIfNull(serverPollingUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverThumbprint);

        var subscriptionId = Guid.NewGuid().ToString("N");

        var cert = CreateSelfSignedCert();
        var scriptService = new LocalScriptService();
        var capsService = new StubCapabilitiesService();
        var runtime = BuildRuntime(cert, scriptService, capsService);

        runtime.Trust(serverThumbprint);

        runtime.Poll(
            new Uri($"poll://{subscriptionId}/"),
            new ServiceEndPoint(serverPollingUri, serverThumbprint, HalibutTimeoutsAndLimits.RecommendedValues()),
            CancellationToken.None);

        return Task.FromResult(new StubAgent(cert, runtime, scriptService, capsService, listeningPort: 0, subscriptionId));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { await _runtime.DisposeAsync().ConfigureAwait(false); } catch { }
        try { _agentCert.Dispose(); } catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static HalibutRuntime BuildRuntime(X509Certificate2 cert, LocalScriptService scriptService, StubCapabilitiesService capsService)
    {
        var asyncScriptAdapter = new AsyncScriptServiceAdapter(scriptService);
        var asyncCapsAdapter = new AsyncCapabilitiesServiceAdapter(capsService);

        var serviceFactory = new DelegateServiceFactory();
        serviceFactory.Register<IScriptService, IScriptServiceAsync>(() => asyncScriptAdapter);
        serviceFactory.Register<ICapabilitiesService, ICapabilitiesServiceAsync>(() => asyncCapsAdapter);

        return new HalibutRuntimeBuilder()
            .WithServiceFactory(serviceFactory)
            .WithServerCertificate(cert)
            .WithHalibutTimeoutsAndLimits(HalibutTimeoutsAndLimits.RecommendedValues())
            .Build();
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest($"CN=stub-tentacle-agent-{Guid.NewGuid():N}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));

        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, string.Empty), string.Empty, X509KeyStorageFlags.Exportable);
    }

    private static int GetEphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Sync → async adapter for IScriptService → IScriptServiceAsync.
    /// LocalScriptService implements the sync IScriptService; Halibut RPC
    /// uses IScriptServiceAsync. The adapter just calls the sync method
    /// inside Task.FromResult — no actual async work happens at this
    /// layer (the script execution itself spawns a background process and
    /// returns immediately, with subsequent GetStatus/CompleteScript polls
    /// reading from the process's output streams).
    /// </summary>
    private sealed class AsyncScriptServiceAdapter : IScriptServiceAsync
    {
        private readonly IScriptService _inner;

        public AsyncScriptServiceAdapter(IScriptService inner) => _inner = inner;

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command, CancellationToken ct)
            => Task.FromResult(_inner.StartScript(command));

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request, CancellationToken ct)
            => Task.FromResult(_inner.GetStatus(request));

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command, CancellationToken ct)
            => Task.FromResult(_inner.CompleteScript(command));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command, CancellationToken ct)
            => Task.FromResult(_inner.CancelScript(command));
    }

    /// <summary>
    /// Stub <see cref="ICapabilitiesService"/> that responds to server-side
    /// capabilities probes. <see cref="AgentVersion"/> is settable so tests
    /// can simulate a tentacle that has upgraded mid-test (probe before →
    /// "1.6.0"; SetAgentVersion("1.7.0"); probe again → "1.7.0").
    ///
    /// <para>Note: <see cref="CapabilitiesRequest"/> implements
    /// <c>IEnumerable&lt;string&gt;</c> per the P0 fix in commit 9ce39b9.
    /// Without that fix, every probe through this stub would throw
    /// <c>ArgumentOutOfRangeException</c> from Halibut's cache-key
    /// generator BEFORE reaching this method. Tests in
    /// <c>HalibutCacheKeyBugReproductionTests</c> pin that the fix
    /// stays in place.</para>
    /// </summary>
    private sealed class StubCapabilitiesService : ICapabilitiesService
    {
        public string AgentVersion { get; set; } = "1.6.0-stub";

        public CapabilitiesResponse GetCapabilities(CapabilitiesRequest request)
            => new()
            {
                AgentVersion = AgentVersion,
                SupportedServices = new List<string> { "IScriptService", "ICapabilitiesService" },
                Metadata = new Dictionary<string, string>
                {
                    ["flavor"] = "stub-tentacle-agent",
                    ["os"] = Environment.OSVersion.Platform.ToString()
                }
            };
    }

    /// <summary>Sync → async adapter for capabilities, mirroring the script-service adapter.</summary>
    private sealed class AsyncCapabilitiesServiceAdapter : ICapabilitiesServiceAsync
    {
        private readonly ICapabilitiesService _inner;

        public AsyncCapabilitiesServiceAdapter(ICapabilitiesService inner) => _inner = inner;

        public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request, CancellationToken ct)
            => Task.FromResult(_inner.GetCapabilities(request));
    }
}
