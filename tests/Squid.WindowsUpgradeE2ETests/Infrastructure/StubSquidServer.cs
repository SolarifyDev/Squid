using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Squid.Message.Contracts.Tentacle;

namespace Squid.WindowsUpgradeE2ETests.Infrastructure;

/// <summary>
/// In-process stub of the Squid server's Halibut + REST surface — used by
/// Phase 12.I+ E2E tests that need a real registration / dispatch target
/// for the production <c>Squid.Tentacle.exe</c> binary WITHOUT dragging in
/// the full <c>Squid.E2ETests</c> Postgres + Kind cluster fixture.
///
/// <para><b>Coverage delta vs <c>Squid.E2ETests.Infrastructure.TentacleStub</c>:</b>
/// TentacleStub is the AGENT-side stub (impersonates a tentacle so tests of
/// the SERVER-side dispatch pipeline can run). StubSquidServer is the
/// inverse — the SERVER-side stub so tests of the AGENT-side binary
/// (register, run as service, receive dispatches) can run without a real
/// Squid server.</para>
///
/// <para><b>What it provides today (Phase 12.H + 12.I):</b></para>
/// <list type="bullet">
///   <item>Self-signed server certificate + thumbprint exposed for
///         <c>--thumbprint X</c> pinning.</item>
///   <item>Halibut polling listener on a unique loopback port.</item>
///   <item>Trust + dispatch helpers (Polling + Listening) for script RPC.</item>
///   <item><b>HTTP REST listener for the <c>register</c> handshake</b>
///         on a separate loopback port. Routes:
///         <c>POST /api/machines/register/tentacle-listening</c> +
///         <c>POST /api/machines/register/tentacle-polling</c>.
///         Default response is a successful registration with the stub's
///         server thumbprint; <see cref="ConfigureRegisterStatusCode"/> +
///         <see cref="ConfigureRegisterBody"/> let tests inject failures.</item>
///   <item><see cref="ReceivedRegistrations"/> records every register call
///         so tests can assert on the request shape (machineName, roles,
///         thumbprint, subscriptionId, etc.).</item>
/// </list>
///
/// <para><b>Not yet provided (Phase 12.J will add):</b></para>
/// <list type="bullet">
///   <item>Capabilities probe response shape — for deploy / upgrade E2E.</item>
///   <item><c>last-upgrade.json</c> reception path — for upgrade E2E.</item>
/// </list>
///
/// <para><b>Lifetime</b>: <see cref="IAsyncDisposable"/>. Each test creates
/// its own instance via <see cref="StartAsync"/>; no shared-fixture pattern
/// (each test gets unique ports + cert so concurrent runs don't collide).</para>
///
/// <para><b>HTTP not HTTPS for the REST endpoint</b>: System.Net.HttpListener
/// can bind plain HTTP on loopback without admin / cert ACL setup, while
/// HTTPS requires <c>netsh http add sslcert</c> + an installed cert. For
/// tests, plain HTTP is simpler and the security validation is covered by
/// the production Listening Tentacle's <c>EnsureSchemeSafeForSecret</c> +
/// its dedicated unit tests. Tests that drive register against this stub
/// set <c>SQUID_REGISTER_HTTPS_ENFORCEMENT=off</c> in their context so the
/// http+secret guard doesn't throw.</para>
///
/// <para><b>Cross-platform</b>: Halibut + HttpListener are .NET-cross-
/// platform. Tests using this fixture run identically on macOS / Linux /
/// Windows.</para>
/// </summary>
public sealed class StubSquidServer : IAsyncDisposable
{
    private readonly X509Certificate2 _serverCert;
    private readonly HalibutRuntime _runtime;
    private readonly HttpListener _httpListener;
    private readonly CancellationTokenSource _httpLoopCts;
    // Not readonly: assigned by StartAsync after construction so the loop
    // can capture `this` for the HandleRequestAsync callback. Single
    // assignment in practice; never mutated again post-StartAsync.
    private Task _httpLoopTask;
    private readonly ConcurrentBag<RegistrationRequest> _receivedRegistrations = new();

    private int _registerStatusCode = 200;
    private string _registerBodyOverride;
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

    /// <summary>The polling listener's port (loopback only).</summary>
    public int PollingPort { get; }

    /// <summary>
    /// The stub's HTTP REST listener URI — used as <c>--server</c> in
    /// register tests. Format: <c>http://localhost:&lt;loopback-port&gt;/</c>.
    /// </summary>
    public Uri ServerUri { get; }

    /// <summary>The REST listener's port (loopback only).</summary>
    public int ServerPort { get; }

    /// <summary>
    /// Underlying Halibut runtime — exposed for Phase 12.J.E+ tests that
    /// need to construct production <see cref="Squid.Core.Services.DeploymentExecution.Transport.IHalibutClientFactory"/>
    /// instances pointing at this stub. Tests that just use
    /// <c>DispatchAndObserve*Async</c> don't need this.
    /// </summary>
    public HalibutRuntime HalibutRuntime => _runtime;

    /// <summary>
    /// Snapshot of every <c>POST /api/machines/register/...</c> call
    /// received since the stub started. Tests assert on the request shape
    /// (machineName, roles, thumbprint, subscriptionId, listening URI, etc.).
    /// </summary>
    public IReadOnlyCollection<RegistrationRequest> ReceivedRegistrations => _receivedRegistrations;

    private StubSquidServer(X509Certificate2 cert, HalibutRuntime runtime, int pollingPort, HttpListener httpListener, int serverPort, CancellationTokenSource httpLoopCts)
    {
        _serverCert = cert;
        _runtime = runtime;
        _httpListener = httpListener;
        _httpLoopCts = httpLoopCts;

        ServerThumbprint = cert.Thumbprint;
        PollingPort = pollingPort;
        PollingUri = new Uri($"https://localhost:{pollingPort}/");
        ServerPort = serverPort;
        ServerUri = new Uri($"http://localhost:{serverPort}/");
    }

    /// <summary>
    /// Starts a stub server on unique loopback ports. Caller MUST dispose
    /// (via <c>await using</c> or explicit <see cref="DisposeAsync"/>) so
    /// Halibut + HttpListener release the ports.
    /// </summary>
    public static async Task<StubSquidServer> StartAsync()
    {
        var cert = CreateSelfSignedCert();
        var runtime = BuildRuntime(cert);

        // Two unique loopback ports — Halibut polling + HTTP REST. Allocate
        // both before either binds so we never see "address in use" on the
        // second one because the first ephemeral-probe race captured it.
        var pollingPort = GetEphemeralPort();
        var serverPort = GetEphemeralPort();

        // Halibut.Listen returns the actually-bound port (caller-port hint
        // is just a suggestion). Re-read authoritatively for the canonical
        // PollingUri.
        var assignedPollingPort = runtime.Listen(pollingPort);

        // HttpListener — must register the URL prefix before Start. Plain
        // HTTP loopback works without admin / netsh urlacl on Windows.
        var httpListener = new HttpListener();
        httpListener.Prefixes.Add($"http://localhost:{serverPort}/");
        httpListener.Start();

        var httpLoopCts = new CancellationTokenSource();
        var server = new StubSquidServer(cert, runtime, assignedPollingPort, httpListener, serverPort, httpLoopCts);

        // Start the listener loop AFTER construction so the loop body can
        // bind `this` for HandleRequestAsync. Single assignment to
        // _httpLoopTask; subsequent code never mutates it.
        server._httpLoopTask = Task.Run(() => server.RunHttpLoopAsync(httpLoopCts.Token));

        await Task.Yield();   // gives the listener loop a chance to start accepting

        return server;
    }

    /// <summary>
    /// Configures the next register response's HTTP status code. Default is
    /// 200 (success). Set to 401 to test "API key rejected", 500 to test
    /// "server error", etc.
    /// </summary>
    public void ConfigureRegisterStatusCode(int statusCode)
    {
        _registerStatusCode = statusCode;
    }

    /// <summary>
    /// Overrides the JSON body returned for register responses. If null,
    /// the stub returns the default success envelope with stub's
    /// thumbprint + a fresh machineId.
    /// </summary>
    public void ConfigureRegisterBody(string jsonBody)
    {
        _registerBodyOverride = jsonBody;
    }

    /// <summary>Adds an agent's thumbprint to the Halibut trust list.</summary>
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
    public async Task<ScriptStatusResponse> DispatchScriptToPollingAgentAsync(string agentSubscriptionId, string agentThumbprint, StartScriptCommand command, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentSubscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentThumbprint);
        ArgumentNullException.ThrowIfNull(command);

        var pollingEndpoint = new ServiceEndPoint(new Uri($"poll://{agentSubscriptionId}/"), agentThumbprint, HalibutTimeoutsAndLimits.RecommendedValues());
        var asyncClient = _runtime.CreateAsyncClient<IScriptService, IAsyncScriptService>(pollingEndpoint);

        // IAsyncScriptService methods don't take CancellationToken (Halibut
        // contract — see IAsyncScriptService doc-comment). Caller's ct is
        // honored at the await level via CancellationToken.None on the RPC.
        return await asyncClient.StartScriptAsync(command).ConfigureAwait(false);
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
        var asyncClient = _runtime.CreateAsyncClient<IScriptService, IAsyncScriptService>(listeningEndpoint);

        return await asyncClient.StartScriptAsync(command).ConfigureAwait(false);
    }

    /// <summary>
    /// Full StartScript → observe → CompleteScript round-trip to a Listening
    /// agent. Mirrors what
    /// <c>HalibutMachineExecutionStrategy.ObserveAndCompleteAsync</c> does
    /// in production: poll <c>GetStatusAsync</c> every 200ms until the
    /// agent reports <see cref="ProcessState.Complete"/>, then call
    /// <c>CompleteScriptAsync</c> for the final logs + exit code.
    /// </summary>
    /// <param name="timeout">Hard cap on the observation loop. Default is
    /// 30 seconds; tests with long scripts should pass a larger value.</param>
    public async Task<ObservedScriptResult> DispatchAndObserveListeningAsync(Uri agentUri, string agentThumbprint, StartScriptCommand command, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agentUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentThumbprint);
        ArgumentNullException.ThrowIfNull(command);

        var endpoint = new ServiceEndPoint(agentUri, agentThumbprint, HalibutTimeoutsAndLimits.RecommendedValues());
        var client = _runtime.CreateAsyncClient<IScriptService, IAsyncScriptService>(endpoint);

        var startResponse = await client.StartScriptAsync(command).ConfigureAwait(false);

        return await ObserveToCompletionAsync(client, command.ScriptTicket, startResponse, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Polling counterpart to <see cref="DispatchAndObserveListeningAsync"/>.
    /// Same flow but the endpoint is <c>poll://&lt;sub-id&gt;/</c> for an
    /// agent that previously dialled in to <see cref="PollingUri"/>.
    /// </summary>
    public async Task<ObservedScriptResult> DispatchAndObservePollingAsync(string agentSubscriptionId, string agentThumbprint, StartScriptCommand command, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentSubscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentThumbprint);
        ArgumentNullException.ThrowIfNull(command);

        var endpoint = new ServiceEndPoint(new Uri($"poll://{agentSubscriptionId}/"), agentThumbprint, HalibutTimeoutsAndLimits.RecommendedValues());
        var client = _runtime.CreateAsyncClient<IScriptService, IAsyncScriptService>(endpoint);

        var startResponse = await client.StartScriptAsync(command).ConfigureAwait(false);

        return await ObserveToCompletionAsync(client, command.ScriptTicket, startResponse, timeout, ct).ConfigureAwait(false);
    }

    private static async Task<ObservedScriptResult> ObserveToCompletionAsync(IAsyncScriptService client, ScriptTicket ticket, ScriptStatusResponse startResponse, TimeSpan timeout, CancellationToken ct)
    {
        var allLogs = new List<ProcessOutput>(startResponse.Logs);
        var nextSeq = startResponse.NextLogSequence;
        var state = startResponse.State;

        var deadline = DateTime.UtcNow + timeout;
        while (state != ProcessState.Complete && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, ct).ConfigureAwait(false);

            var status = await client.GetStatusAsync(new ScriptStatusRequest(ticket, nextSeq)).ConfigureAwait(false);
            allLogs.AddRange(status.Logs);
            nextSeq = status.NextLogSequence;
            state = status.State;
        }

        if (state != ProcessState.Complete)
            throw new TimeoutException(
                $"script {ticket.TaskId} did not reach ProcessState.Complete within {timeout}. " +
                $"Final state: {state}. Logs so far:\n" +
                string.Join("\n", allLogs.Select(l => $"[{l.Source}] {l.Text}")));

        var completeResponse = await client.CompleteScriptAsync(new CompleteScriptCommand(ticket, nextSeq)).ConfigureAwait(false);
        allLogs.AddRange(completeResponse.Logs);

        return new ObservedScriptResult
        {
            ExitCode = completeResponse.ExitCode,
            State = state,
            AllLogs = allLogs,
            AllText = string.Join("\n", allLogs.Select(l => l.Text))
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _httpLoopCts.Cancel(); } catch { /* best-effort */ }
        try { _httpListener.Stop(); _httpListener.Close(); } catch { /* best-effort */ }
        try { if (_httpLoopTask != null) await _httpLoopTask.ConfigureAwait(false); } catch { /* loop drains on cancel */ }
        try { await _runtime.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        try { _serverCert.Dispose(); } catch { /* best-effort */ }
    }

    // ── HTTP REST loop ────────────────────────────────────────────────────────

    /// <summary>
    /// Drains incoming HTTP requests from the listener. One thread, one
    /// loop — register flows are sequential per-test, so no need for
    /// per-request thread pool overhead. Ignores OperationCanceledException
    /// + ObjectDisposedException raised by <see cref="DisposeAsync"/>.
    /// </summary>
    private async Task RunHttpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _httpListener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) { return; }

            try { await HandleRequestAsync(ctx).ConfigureAwait(false); }
            catch
            {
                // Best-effort response writer; if the test made an
                // assertion that blew up here, the stub doesn't crash the
                // listener loop. The next request still gets handled.
                try
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.OutputStream.Close();
                }
                catch { }
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;
        var method = ctx.Request.HttpMethod;

        if (method == "POST" && path == "/api/machines/register/tentacle-listening")
        {
            await HandleRegisterAsync(ctx, RegistrationKind.Listening).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path == "/api/machines/register/tentacle-polling")
        {
            await HandleRegisterAsync(ctx, RegistrationKind.Polling).ConfigureAwait(false);
            return;
        }

        // Unknown path — 404. Helps tests that intentionally hit a wrong
        // path catch the regression vs landing in the register-success
        // branch silently.
        ctx.Response.StatusCode = 404;
        ctx.Response.OutputStream.Close();
    }

    private async Task HandleRegisterAsync(HttpListenerContext ctx, RegistrationKind kind)
    {
        // Read body for assertion + parse machineName / roles / etc.
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        var apiKey = ctx.Request.Headers["X-API-KEY"];
        var bearerHeader = ctx.Request.Headers["Authorization"];

        var record = ParseRegistration(kind, body, apiKey, bearerHeader);
        _receivedRegistrations.Add(record);

        // Write configured response.
        ctx.Response.StatusCode = _registerStatusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";

        var responseJson = _registerBodyOverride ?? BuildDefaultRegisterResponseJson(record);
        var responseBytes = Encoding.UTF8.GetBytes(responseJson);

        await ctx.Response.OutputStream.WriteAsync(responseBytes).ConfigureAwait(false);
        ctx.Response.OutputStream.Close();
    }

    private RegistrationRequest ParseRegistration(RegistrationKind kind, string body, string apiKey, string bearerHeader)
    {
        var record = new RegistrationRequest
        {
            Kind = kind,
            RawBody = body,
            ApiKeyHeader = apiKey,
            BearerHeader = bearerHeader
        };

        if (string.IsNullOrWhiteSpace(body)) return record;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("machineName", out var mn)) record.MachineName = mn.GetString();
            if (root.TryGetProperty("thumbprint", out var th)) record.AgentThumbprint = th.GetString();
            if (root.TryGetProperty("subscriptionId", out var s)) record.SubscriptionId = s.GetString();
            if (root.TryGetProperty("uri", out var u)) record.ListeningUri = u.GetString();
            if (root.TryGetProperty("spaceId", out var sp)) record.SpaceId = sp.GetInt32();
            if (root.TryGetProperty("roles", out var r)) record.Roles = r.GetString();
            if (root.TryGetProperty("environments", out var e)) record.Environments = e.GetString();
            if (root.TryGetProperty("agentVersion", out var av)) record.AgentVersion = av.GetString();
        }
        catch { /* unparseable body is itself an assertion target */ }

        return record;
    }

    private string BuildDefaultRegisterResponseJson(RegistrationRequest req)
    {
        // Production server response shape (per
        // src/Squid.Tentacle/Registration/TentacleRegistrationClient.cs):
        //   { "code": 200, "msg": "ok", "data": { machineId, serverThumbprint, subscriptionUri } }
        // Polling tentacles get subscriptionUri = poll://<sub>/; Listening
        // tentacles get an empty/optional subscriptionUri.
        var subscriptionUri = req.Kind == RegistrationKind.Polling && !string.IsNullOrEmpty(req.SubscriptionId)
            ? $"poll://{req.SubscriptionId}/"
            : string.Empty;

        // machineId is a positive int; use 1 + the ordinal of registrations
        // received so tests can match request to response by index.
        var machineId = _receivedRegistrations.Count;

        var response = new
        {
            code = 200,
            msg = "ok",
            data = new
            {
                machineId,
                serverThumbprint = ServerThumbprint,
                subscriptionUri
            }
        };

        return JsonSerializer.Serialize(response);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static HalibutRuntime BuildRuntime(X509Certificate2 cert)
    {
        var emptyServiceFactory = new DelegateServiceFactory();

        return new HalibutRuntimeBuilder()
            .WithServiceFactory(emptyServiceFactory)
            .WithServerCertificate(cert)
            .WithHalibutTimeoutsAndLimits(HalibutTimeoutsAndLimits.RecommendedValues())
            .Build();
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest($"CN=stub-squid-server-{Guid.NewGuid():N}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

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
}

/// <summary>Polling vs Listening registration variants (matching the production endpoint paths).</summary>
public enum RegistrationKind
{
    Polling,
    Listening
}

/// <summary>
/// Outcome of a full StartScript → observe → CompleteScript round-trip.
/// <see cref="State"/> is <see cref="ProcessState.Complete"/> on success;
/// the observation helper throws TimeoutException if the agent never
/// reaches Complete within the supplied window, so callers don't need a
/// "still Running" branch.
/// </summary>
public sealed class ObservedScriptResult
{
    /// <summary>Final exit code from the agent's script process.</summary>
    public int ExitCode { get; init; }

    /// <summary>Process state — always Complete; included for symmetry with production result types.</summary>
    public ProcessState State { get; init; }

    /// <summary>All log lines from StartScript + every GetStatus poll + CompleteScript, in order received.</summary>
    public List<ProcessOutput> AllLogs { get; init; }

    /// <summary>Concatenated newline-joined text of every log line — for substring assertions.</summary>
    public string AllText { get; init; }
}

/// <summary>
/// Snapshot of one <c>POST /api/machines/register/...</c> call. Tests assert
/// on individual fields after registering — e.g. "the agent sent its
/// thumbprint" / "the API key header was set" / "the listening URI was
/// reported correctly".
/// </summary>
public sealed class RegistrationRequest
{
    public RegistrationKind Kind { get; init; }
    public string RawBody { get; init; }
    public string ApiKeyHeader { get; init; }
    public string BearerHeader { get; init; }

    public string MachineName { get; set; }
    public string AgentThumbprint { get; set; }
    public string SubscriptionId { get; set; }      // Polling only
    public string ListeningUri { get; set; }         // Listening only
    public int SpaceId { get; set; }
    public string Roles { get; set; }
    public string Environments { get; set; }
    public string AgentVersion { get; set; }
}
