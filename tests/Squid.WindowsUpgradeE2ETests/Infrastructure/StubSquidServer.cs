using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Squid.Message.Contracts.Tentacle;

namespace Squid.WindowsUpgradeE2ETests.Infrastructure;

/// <summary>
/// In-process stub of the Squid server's Halibut surface — used by Phase
/// 12.I+ E2E tests that need a real registration/dispatch target for the
/// production <c>Squid.Tentacle.exe</c> binary WITHOUT dragging in the
/// full <c>Squid.E2ETests</c> Postgres + Kind cluster fixture.
///
/// <para><b>Coverage delta vs <c>Squid.E2ETests.Infrastructure.TentacleStub</c>:</b>
/// TentacleStub is the AGENT-side stub (impersonates a tentacle so tests of
/// the SERVER-side dispatch pipeline can run). StubSquidServer is the
/// inverse — the SERVER-side stub so tests of the AGENT-side binary
/// (register, run as service, receive dispatches) can run without a real
/// Squid server. Both fixtures use the same Halibut runtime infrastructure
/// but expose opposite roles.</para>
///
/// <para><b>What it provides today (Phase 12.H scaffold):</b></para>
/// <list type="bullet">
///   <item>A self-signed server certificate + thumbprint exposed to tests
///         so they can pin via <c>squid-tentacle register --thumbprint X</c>.</item>
///   <item>A Halibut polling listener on a unique loopback port.</item>
///   <item>Trust-agent helpers so polling tentacles can be registered.</item>
///   <item>Dispatch helpers (Polling + Listening) so tests can send a
///         <c>StartScriptCommand</c> through the production Halibut path
///         and observe the response.</item>
/// </list>
///
/// <para><b>Not yet provided (Phase 12.I will add):</b></para>
/// <list type="bullet">
///   <item>REST endpoint for the <c>register</c> handshake — Squid server's
///         current registration shape uses an HTTP REST endpoint to issue
///         the server's thumbprint + accept the agent's identity. Shape is
///         server-dependent and will be reverse-engineered + stubbed when
///         Phase 12.I (register E2E) lands.</item>
///   <item>Capabilities probe response shape — Phase 12.J (deploy + upgrade
///         E2E) will add the response builder so server can read the
///         tentacle's reported version + flavor.</item>
///   <item><c>last-upgrade.json</c> reception path — Phase 12.J will wire
///         the upgrade flow so server reads the agent's status report.</item>
/// </list>
///
/// <para><b>Lifetime</b>: <see cref="IAsyncDisposable"/>. Each test creates
/// its own instance via <see cref="StartAsync"/>; no shared-fixture pattern
/// (each test gets a unique port + cert so concurrent runs don't collide).</para>
///
/// <para><b>Cross-platform</b>: Halibut is .NET-cross-platform. This stub
/// runs on Windows / Linux / macOS. The OS-specific concerns are in the
/// CALLER (the test class running the production tentacle binary against
/// the stub), not in the stub itself.</para>
/// </summary>
public sealed class StubSquidServer : IAsyncDisposable
{
    private readonly X509Certificate2 _serverCert;
    private readonly HalibutRuntime _runtime;
    private bool _disposed;

    /// <summary>
    /// SHA-1 thumbprint of the stub's self-signed server certificate. The
    /// production <c>squid-tentacle register --thumbprint X</c> CLI pins
    /// this to skip TLS issuer validation.
    /// </summary>
    public string ServerThumbprint { get; }

    /// <summary>
    /// The stub's Halibut polling listener URI — agents in Polling mode
    /// dial in here. Format: <c>https://localhost:&lt;loopback-port&gt;/</c>.
    /// </summary>
    public Uri PollingUri { get; }

    /// <summary>
    /// The polling listener's port (loopback only). Exposed for tests that
    /// need to log it for diagnostics or compose URIs differently.
    /// </summary>
    public int PollingPort { get; }

    private StubSquidServer(X509Certificate2 cert, HalibutRuntime runtime, int pollingPort)
    {
        _serverCert = cert;
        _runtime = runtime;
        ServerThumbprint = cert.Thumbprint;
        PollingPort = pollingPort;
        PollingUri = new Uri($"https://localhost:{pollingPort}/");
    }

    /// <summary>
    /// Starts a stub server on a unique loopback port. Caller MUST dispose
    /// the returned instance (via <c>await using</c> or explicit
    /// <see cref="DisposeAsync"/>) so the Halibut runtime + listener
    /// release the port.
    /// </summary>
    public static Task<StubSquidServer> StartAsync()
    {
        var cert = CreateSelfSignedCert();
        var runtime = BuildRuntime(cert);

        // Port allocation: ask the OS for a free loopback port (Rule 12.8).
        // Halibut.Listen() takes a port and returns the actual bound port —
        // we use that to construct the canonical PollingUri so tests don't
        // race a stale "before-bind" port number.
        var assignedPort = runtime.Listen(GetEphemeralPort());

        var server = new StubSquidServer(cert, runtime, assignedPort);

        return Task.FromResult(server);
    }

    /// <summary>
    /// Adds an agent's thumbprint to the trust list. Polling agents must be
    /// trusted before their <c>poll://</c> connection is accepted; without
    /// this call, Halibut would reject the agent's certificate at TLS
    /// handshake time.
    /// </summary>
    public void TrustAgent(string agentThumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentThumbprint);
        _runtime.Trust(agentThumbprint);
    }

    /// <summary>
    /// Dispatches a script to a polling agent that has previously connected
    /// to this stub's <see cref="PollingUri"/>. Blocks until the agent
    /// returns a status response. Mirrors what the production server's
    /// <c>HalibutMachineExecutionStrategy.ExecuteScriptAsync</c> does for a
    /// polling endpoint.
    /// </summary>
    /// <param name="agentSubscriptionId">The agent's subscription ID
    /// (matches the GUID in the agent's <c>poll://&lt;sub&gt;/</c> URI).</param>
    /// <param name="agentThumbprint">The agent's certificate thumbprint
    /// (must have been trusted via <see cref="TrustAgent"/> first).</param>
    /// <param name="command">The script command to dispatch.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ScriptStatusResponse> DispatchScriptToPollingAgentAsync(string agentSubscriptionId, string agentThumbprint, StartScriptCommand command, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentSubscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentThumbprint);
        ArgumentNullException.ThrowIfNull(command);

        var pollingEndpoint = new ServiceEndPoint(new Uri($"poll://{agentSubscriptionId}/"), agentThumbprint, HalibutTimeoutsAndLimits.RecommendedValues());
        var asyncClient = _runtime.CreateAsyncClient<IScriptService, IScriptServiceAsync>(pollingEndpoint);

        return await asyncClient.StartScriptAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatches a script to a listening agent at the given URI. Blocks
    /// until the agent returns a status response.
    /// </summary>
    public async Task<ScriptStatusResponse> DispatchScriptToListeningAgentAsync(Uri agentUri, string agentThumbprint, StartScriptCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agentUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentThumbprint);
        ArgumentNullException.ThrowIfNull(command);

        var listeningEndpoint = new ServiceEndPoint(agentUri, agentThumbprint, HalibutTimeoutsAndLimits.RecommendedValues());
        var asyncClient = _runtime.CreateAsyncClient<IScriptService, IScriptServiceAsync>(listeningEndpoint);

        return await asyncClient.StartScriptAsync(command, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { await _runtime.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        try { _serverCert.Dispose(); } catch { /* best-effort */ }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a Halibut runtime configured as a server: it accepts incoming
    /// polling connections AND can issue outbound RPC to listening agents.
    /// No service factory registered (server side doesn't HOST RPC services
    /// — it CONSUMES them via outbound RPC clients).
    /// </summary>
    private static HalibutRuntime BuildRuntime(X509Certificate2 cert)
    {
        var emptyServiceFactory = new DelegateServiceFactory();

        return new HalibutRuntimeBuilder()
            .WithServiceFactory(emptyServiceFactory)
            .WithServerCertificate(cert)
            .WithHalibutTimeoutsAndLimits(HalibutTimeoutsAndLimits.RecommendedValues())
            .Build();
    }

    /// <summary>
    /// Self-signed certificate for the stub. Subject = unique GUID so
    /// concurrent stubs don't share thumbprints (rare but possible if RSA
    /// generates the same key twice in a tight loop).
    /// </summary>
    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest($"CN=stub-squid-server-{Guid.NewGuid():N}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));

        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, string.Empty), string.Empty, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// Asks the OS for a free loopback port (Rule 12.8). Closes the
    /// listener immediately so Halibut can rebind to the same port; there's
    /// a microscopic race where another process could grab it in between,
    /// but Halibut.Listen returns the bound port so we re-read it
    /// authoritatively after .Listen() succeeds.
    /// </summary>
    private static int GetEphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
