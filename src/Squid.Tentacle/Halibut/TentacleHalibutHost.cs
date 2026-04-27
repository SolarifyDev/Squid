using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Squid.Tentacle.Configuration;
using Squid.Message.Contracts.Tentacle;
using Serilog;

namespace Squid.Tentacle.Halibut;

public class TentacleHalibutHost : ITentacleHalibutHost
{
    /// <summary>
    /// P1-T.10 (Phase-8): env var that selects the IP address the listening
    /// Halibut runtime binds to. Default (unset / blank / "any") is
    /// <c>IPAddress.Any</c> (0.0.0.0) — listen on all interfaces, matching
    /// pre-fix behaviour. Operators on multi-NIC hosts or strict-firewall
    /// environments can pin to <c>127.0.0.1</c> (loopback-only) or a
    /// specific interface IP. Unrecognised / unparseable values fall back
    /// to <c>IPAddress.Any</c> with a warning so a typo never silently
    /// makes the agent invisible on the network.
    ///
    /// <para>Pinned literal — renaming breaks operators who set this in
    /// their deployment manifest / systemd unit.</para>
    /// </summary>
    public const string ListenIpAddressEnvVar = "SQUID_TENTACLE_LISTEN_IP_ADDRESS";

    private readonly HalibutRuntime _runtime;
    private readonly TentacleSettings _settings;

    // P0-T.4: signals the Halibut polling loop to stop picking up new RPCs on drain.
    // Replaces the pre-fix `CancellationToken.None` wired into every Poll() call.
    private readonly CancellationTokenSource _pollCts = new();

    public bool IsListening { get; private set; }
    public int ListeningPort { get; private set; }

    /// <summary>
    /// The cancellation token threaded into every <c>HalibutRuntime.Poll</c> call.
    /// Exposed <c>internal</c> so unit tests can assert the cancel wiring without
    /// standing up a real Halibut runtime.
    /// </summary>
    internal CancellationToken PollCancellationToken => _pollCts.Token;

    public TentacleHalibutHost(
        X509Certificate2 tentacleCert,
        IScriptService scriptService,
        TentacleSettings settings,
        ICapabilitiesService capabilitiesService = null,
        IFileTransferService fileTransferService = null)
    {
        _settings = settings;

        var asyncAdapter = new AsyncScriptServiceAdapter(scriptService);
        var capsAdapter = new AsyncCapabilitiesServiceAdapter(capabilitiesService ?? new Core.CapabilitiesService());

        // P1-Phase9b.3: register the file-transfer service. Default impl writes
        // under ~/.squid/uploads with workspace-boundary path rewriting (rooted
        // / traversal paths are sanitised to a hash-derived filename).
        var fileTransfer = fileTransferService ?? new FileTransfer.LocalFileTransferService();
        var fileTransferAdapter = new AsyncFileTransferServiceAdapter(fileTransfer);

        var serviceFactory = new DelegateServiceFactory();
        serviceFactory.Register<IScriptService, IScriptServiceAsync>(() => asyncAdapter);
        serviceFactory.Register<ICapabilitiesService, ICapabilitiesServiceAsync>(() => capsAdapter);
        serviceFactory.Register<IFileTransferService, IFileTransferServiceAsync>(() => fileTransferAdapter);

        _runtime = new HalibutRuntimeBuilder()
            .WithServiceFactory(serviceFactory)
            .WithServerCertificate(tentacleCert)
            .WithHalibutTimeoutsAndLimits(HalibutTimeoutsAndLimits.RecommendedValues())
            .Build();
    }

    /// <summary>
    /// P1-Phase9.15 — startup-jitter env var for thundering-herd mitigation.
    ///
    /// <para><b>Problem</b>: when the server restarts, all N polling Tentacles
    /// detect the connection drop simultaneously and try to reconnect at the
    /// same instant. Halibut's accept queue saturates; first wave of agents
    /// gets connection-refused or timeout; deployments fail with "server
    /// unreachable" even though server is healthy.</para>
    ///
    /// <para><b>Fix</b>: each Tentacle waits a uniformly-random
    /// [0, JitterMaxMs] ms before invoking its first Poll(). Operators tune
    /// the upper bound to match their fleet size — for 1000 agents, set
    /// 30000ms (30s) so the reconnect storm is spread over ~30s instead of
    /// arriving in a single second.</para>
    ///
    /// <para>Default 0ms preserves pre-Phase-9.15 behaviour (no jitter) for
    /// small fleets where the storm wouldn't matter. Operators with bigger
    /// fleets opt-in by setting the env var.</para>
    /// </summary>
    public const string PollingStartupJitterEnvVar = "SQUID_TENTACLE_POLLING_STARTUP_JITTER_MS";

    public const int DefaultPollingStartupJitterMs = 0;
    public const int MaxPollingStartupJitterMs = 5 * 60 * 1000;  // 5 min — sanity cap

    public void StartPolling(string serverThumbprint, string subscriptionId, string subscriptionUri = null)
    {
        // P1-Phase9.15: jitter the start of polling so reconnect storms after
        // server restart don't arrive in a single instant. Read env var on
        // every call (cheap, allows operators to flip without restart).
        ApplyStartupJitter();

        // ServerCertificate may be a comma-separated list (Octopus-aligned multi-server trust).
        // Every listed thumbprint is Trust()ed so cert-rotation windows where old+new coexist
        // don't break running Tentacles.
        var trusted = Squid.Tentacle.Certificate.ServerCertificateValidator.ParseThumbprints(serverThumbprint);

        foreach (var thumbprint in trusted)
            _runtime.Trust(thumbprint);

        // Primary thumbprint (first in list) is used for the ServiceEndPoint TLS pinning.
        // Halibut's ServiceEndPoint only accepts one thumbprint per endpoint, so if callers
        // want true multi-server trust they must also configure multiple ServerCommsAddresses.
        var primaryThumbprint = trusted.Count > 0 ? trusted[0] : serverThumbprint;

        var pollUri = ResolvePollUri(subscriptionId, subscriptionUri);
        var serverUrls = _settings.GetServerCommsUrls();

        if (serverUrls.Count == 0)
            throw new InvalidOperationException("No server comms URLs configured. Set ServerCommsUrl or ServerCommsAddresses.");

        // Clamp to 1..8 matching the Octopus upper bound — more than 8 concurrent
        // polling connections per server bring diminishing returns and mostly add
        // server-side resource pressure. One connection is the minimum to receive
        // any work; raising the default to 5 is what avoids the single-RPC head-of-
        // line blocking scenario where one stuck GetStatus starves all other calls.
        const int MinConnections = 1;
        const int MaxConnections = 8;
        var connectionCount = Math.Clamp(_settings.PollingConnectionCount, MinConnections, MaxConnections);
        if (connectionCount != _settings.PollingConnectionCount)
        {
            Log.Warning("Configured PollingConnectionCount={Configured} clamped to {Actual} (allowed range {Min}-{Max})",
                _settings.PollingConnectionCount, connectionCount, MinConnections, MaxConnections);
        }

        var totalConnections = 0;
        var halibutProxy = ProxyConfigurationBuilder.BuildHalibutProxy(_settings.Proxy);

        foreach (var serverUrl in serverUrls)
        {
            var pollingEndpointUri = new Uri(serverUrl);

            WarnIfCommsUrlMatchesApiUrl(pollingEndpointUri);

            var serverEndpoint = new ServiceEndPoint(
                pollingEndpointUri,
                primaryThumbprint,
                halibutProxy,                                           // null = direct connection
                HalibutTimeoutsAndLimits.RecommendedValues());

            for (var i = 0; i < connectionCount; i++)
            {
                // P0-T.4: use the host's own CT so CancelPolling() can stop the loop
                // cleanly during drain. Pre-fix this was CancellationToken.None and
                // the only way to stop polling was disposing the runtime — which
                // races with in-flight RPCs.
                _runtime.Poll(pollUri, serverEndpoint, _pollCts.Token);
                totalConnections++;
            }
        }

        Log.Information(
            "Halibut polling started. SubscriptionId={SubscriptionId}, ServerUrls={ServerUrlCount}, ConnectionsPerServer={ConnectionCount}, TotalConnections={TotalConnections}, Proxy={Proxy}",
            subscriptionId, serverUrls.Count, connectionCount, totalConnections,
            halibutProxy == null ? "direct" : $"{_settings.Proxy.Host}:{_settings.Proxy.Port}");
    }

    private void WarnIfCommsUrlMatchesApiUrl(Uri commsUri)
    {
        if (string.IsNullOrWhiteSpace(_settings.ServerUrl)) return;

        try
        {
            var apiUri = new Uri(_settings.ServerUrl);

            if (string.Equals(apiUri.Host, commsUri.Host, StringComparison.OrdinalIgnoreCase) &&
                apiUri.Port == commsUri.Port)
            {
                Log.Warning(
                    "ServerCommsUrl ({ServerCommsUrl}) has the same host:port as ServerUrl ({ServerUrl}). " +
                    "ServerCommsUrl should point to the Halibut polling port (default 10943), not the HTTP API port",
                    commsUri, apiUri);
            }
        }
        catch (UriFormatException)
        {
            // ServerUrl is invalid, skip comparison
        }
    }

    /// <summary>
    /// P1-Phase9b.4 — hot-reloads the server-certificate trust list without
    /// restarting the agent.
    ///
    /// <para><b>Why this exists</b>: pre-Phase-9b.4, when the operator
    /// rotated the server's TLS certificate, every running Tentacle
    /// continued trusting only the OLD thumbprint. Even though the agent's
    /// config file had been updated with the new thumbprint, the in-process
    /// <see cref="HalibutRuntime"/>'s trust list was loaded ONCE at startup.
    /// New TLS handshakes failed; operators were forced to restart every
    /// Tentacle in the fleet — a maintenance window with downtime.</para>
    ///
    /// <para><b>Contract</b>: <see cref="ReloadTrustList"/> calls
    /// <see cref="HalibutRuntime.TrustOnly"/> with the resolved thumbprint
    /// list — this REPLACES the trust list atomically (in-flight handshakes
    /// continue but new ones use the new list). Empty / null input is a
    /// no-op (we never want to silently un-trust everything).</para>
    ///
    /// <para>SIGHUP wiring is in <see cref="HookConfigReloadOnSighup"/>.</para>
    /// </summary>
    public void ReloadTrustList(string serverCertificate)
    {
        if (string.IsNullOrWhiteSpace(serverCertificate))
        {
            Log.Warning(
                "[CONFIG-RELOAD] Empty serverCertificate — refusing to wipe the trust list. " +
                "If you really want to clear trust, restart the agent without ServerCertificate set.");
            return;
        }

        var newThumbprints = Squid.Tentacle.Certificate.ServerCertificateValidator.ParseThumbprints(serverCertificate);

        if (newThumbprints.Count == 0)
        {
            Log.Warning(
                "[CONFIG-RELOAD] ParseThumbprints returned empty — input '{Input}' was malformed. Trust list unchanged.",
                serverCertificate);
            return;
        }

        _runtime.TrustOnly(newThumbprints);

        Log.Information(
            "[CONFIG-RELOAD] Trust list updated to {Count} thumbprint(s): [{First}...]",
            newThumbprints.Count, newThumbprints[0]);
    }

    /// <summary>
    /// P1-Phase9b.4 — registers a SIGHUP handler that reloads the
    /// <c>ServerCertificate</c> setting from the live config file and applies
    /// it via <see cref="ReloadTrustList"/>.
    ///
    /// <para><b>Operator workflow</b>:
    /// <list type="number">
    ///   <item>Edit the agent's config: update <c>Tentacle:ServerCertificate</c>
    ///         (or <c>TENTACLE__SERVERCERTIFICATE</c> env var) with the new
    ///         server thumbprint(s).</item>
    ///   <item><c>kill -HUP $(pgrep squid-tentacle)</c> — or
    ///         <c>systemctl reload squid-tentacle</c>.</item>
    ///   <item>Agent re-reads config, calls <c>HalibutRuntime.TrustOnly</c>
    ///         with the new thumbprints. No restart, no dropped polls.</item>
    /// </list></para>
    ///
    /// <para><b>Platform note</b>: <see cref="PosixSignalRegistration"/>
    /// supports <c>SIGHUP</c> only on Linux + macOS. On Windows the call
    /// throws <see cref="PlatformNotSupportedException"/> — we catch that
    /// silently. Windows operators have no native SIGHUP equivalent; the
    /// agent's <c>service reload</c> CLI command (Phase-9b.5) is the
    /// platform-portable alternative.</para>
    ///
    /// <para>Returns the registration so the caller can dispose it on
    /// shutdown — never call this from outside the host's lifecycle path.</para>
    /// </summary>
    public IDisposable HookConfigReloadOnSighup(Func<string> readServerCertificate)
    {
        if (readServerCertificate == null) throw new ArgumentNullException(nameof(readServerCertificate));

        try
        {
            return PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
            {
                Log.Information("[CONFIG-RELOAD] SIGHUP received — reloading config.");
                ctx.Cancel = true;  // we handled it; don't terminate the process

                try
                {
                    var newCert = readServerCertificate();
                    ReloadTrustList(newCert);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[CONFIG-RELOAD] Reload failed; trust list unchanged from previous state.");
                }
            });
        }
        catch (PlatformNotSupportedException)
        {
            Log.Information(
                "[CONFIG-RELOAD] SIGHUP handler not registered (platform doesn't support PosixSignal.SIGHUP — likely Windows). " +
                "Operators on this platform must use 'tentacle service reload' or a service restart.");
            return new NoOpDisposable();
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }

    public void StartListening(int port, string serverThumbprint = null)
    {
        var trusted = Squid.Tentacle.Certificate.ServerCertificateValidator.ParseThumbprints(serverThumbprint);

        foreach (var thumbprint in trusted)
        {
            _runtime.Trust(thumbprint);
            Log.Information("Trusted server thumbprint {Thumbprint} for listening mode", thumbprint);
        }

        // P1-T.10 (Phase-8): bind IP resolved from env var. Default IPAddress.Any
        // preserves pre-fix behaviour. HalibutRuntime.Listen(IPEndPoint) returns
        // the actual bound port — important when port=0 (kernel-assigned
        // ephemeral) and useful even when explicit, because it confirms the
        // bind succeeded. The returned int is what we expose to /health/readyz.
        var bindAddress = ResolveListenIpAddress();
        var endpoint = new System.Net.IPEndPoint(bindAddress, port);
        var boundPort = _runtime.Listen(endpoint);

        ListeningPort = boundPort;
        IsListening = true;

        Log.Information("Halibut listening on {BindAddress}:{Port} (trusted {Count} server thumbprint(s))",
            bindAddress, boundPort, trusted.Count);
    }

    /// <summary>
    /// Resolves the listen-IP from <see cref="ListenIpAddressEnvVar"/>.
    /// Default / blank / "any" → <see cref="System.Net.IPAddress.Any"/>.
    /// Unparseable values log a warning and fall back to Any.
    /// </summary>
    internal static System.Net.IPAddress ResolveListenIpAddress()
        => ParseListenIpAddress(System.Environment.GetEnvironmentVariable(ListenIpAddressEnvVar));

    /// <summary>
    /// P1-Phase9.15 — sleep 0..jitterMs (uniformly random) before polling
    /// startup. Reads <see cref="PollingStartupJitterEnvVar"/> at every
    /// invocation so operators can flip it without service restart.
    ///
    /// <para>Out-of-range / unparseable input falls back silently to 0
    /// (no jitter, identical to pre-Phase-9.15 behaviour) — we don't WANT
    /// to crash an operator's Tentacle on a typo.</para>
    /// </summary>
    private static void ApplyStartupJitter()
    {
        var jitterMs = ResolveStartupJitterMs();
        if (jitterMs <= 0) return;

        var randomDelay = System.Random.Shared.Next(0, jitterMs + 1);
        Log.Information(
            "Polling startup jitter: sleeping {DelayMs}ms (max {MaxMs}ms via {EnvVar}) " +
            "to spread reconnect storm across the fleet.",
            randomDelay, jitterMs, PollingStartupJitterEnvVar);
        System.Threading.Thread.Sleep(randomDelay);
    }

    /// <summary>
    /// Pure parser exposed for unit testing without process-level env state.
    /// Returns the resolved upper-bound in ms, clamped to
    /// <c>[0, MaxPollingStartupJitterMs]</c>.
    /// </summary>
    internal static int ParseStartupJitterMs(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultPollingStartupJitterMs;

        if (!int.TryParse(raw.Trim(), out var ms) || ms < 0)
        {
            Log.Warning(
                "{EnvVar}='{RawValue}' is not a valid non-negative integer (ms); falling back to default {Default}.",
                PollingStartupJitterEnvVar, raw, DefaultPollingStartupJitterMs);
            return DefaultPollingStartupJitterMs;
        }

        if (ms > MaxPollingStartupJitterMs)
        {
            Log.Warning(
                "{EnvVar}={RawValue}ms exceeds sanity cap {MaxMs}ms; clamping. " +
                "If you really want a longer jitter window, raise the cap.",
                PollingStartupJitterEnvVar, ms, MaxPollingStartupJitterMs);
            return MaxPollingStartupJitterMs;
        }

        return ms;
    }

    private static int ResolveStartupJitterMs()
        => ParseStartupJitterMs(System.Environment.GetEnvironmentVariable(PollingStartupJitterEnvVar));

    /// <summary>Pure parser for unit testing without env state.</summary>
    internal static System.Net.IPAddress ParseListenIpAddress(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return System.Net.IPAddress.Any;

        var trimmed = raw.Trim();
        if (string.Equals(trimmed, "any", StringComparison.OrdinalIgnoreCase))
            return System.Net.IPAddress.Any;

        if (System.Net.IPAddress.TryParse(trimmed, out var parsed))
            return parsed;

        // Don't crash startup on a typo — log + fall back to Any so the
        // agent is reachable. Operator sees the warning and corrects.
        Log.Warning(
            "Could not parse {EnvVar}={RawValue} as an IP address; falling back to IPAddress.Any " +
            "(0.0.0.0). Set the value to 'any', a valid IPv4/IPv6 literal, or leave unset.",
            ListenIpAddressEnvVar, raw);
        return System.Net.IPAddress.Any;
    }

    /// <summary>
    /// Resolves the polling URI for the agent. Server-returned
    /// <paramref name="subscriptionUri"/> is preferred when present, else
    /// falls back to the deterministic <c>poll://{subscriptionId}/</c>.
    ///
    /// <para><b>P1-T.14 (Phase-8)</b>: a malformed server-returned URI used
    /// to throw <see cref="UriFormatException"/> from <c>new Uri(...)</c>,
    /// crashing tentacle startup with no clean recovery. A buggy server
    /// release would brick every fresh-registering agent. Now we catch
    /// malformed input, log a structured warning naming the offending
    /// value (operator can chase the server bug), and fall back to the
    /// deterministic local form so polling still starts.</para>
    ///
    /// <para>"Malformed" includes BOTH constructor-throwing inputs AND
    /// relative URIs — Halibut requires an absolute URI to know where to
    /// poll, so a relative <c>/just/a/path</c> from a buggy server is
    /// equally useless.</para>
    /// </summary>
    public static Uri ResolvePollUri(string subscriptionId, string subscriptionUri)
    {
        if (string.IsNullOrWhiteSpace(subscriptionUri))
            return new Uri($"poll://{subscriptionId}/");

        if (Uri.TryCreate(subscriptionUri, UriKind.Absolute, out var serverUri))
            return serverUri;

        Log.Warning(
            "Malformed server-returned subscriptionUri {SubscriptionUri} for subscription {SubscriptionId}; " +
            "falling back to deterministic poll://{SubscriptionId}/. The server release likely has a bug; " +
            "agent will still start polling using its own subscription id.",
            subscriptionUri, subscriptionId, subscriptionId);

        return new Uri($"poll://{subscriptionId}/");
    }


    public void CancelPolling()
    {
        // Idempotent — CancellationTokenSource.Cancel is documented safe to call
        // multiple times but will no-op after the first. The IsCancellationRequested
        // check avoids the (harmless) state transition notifications in logs.
        if (!_pollCts.IsCancellationRequested)
            _pollCts.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        // Cancel polling first so in-flight RPCs complete rather than racing the
        // runtime disposal. Matches the shutdown sequence documented in
        // TentacleApp.RunAsync (cancel → drain → dispose).
        CancelPolling();
        await _runtime.DisposeAsync().ConfigureAwait(false);
        _pollCts.Dispose();
    }

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

    private sealed class AsyncCapabilitiesServiceAdapter : ICapabilitiesServiceAsync
    {
        private readonly ICapabilitiesService _inner;

        public AsyncCapabilitiesServiceAdapter(ICapabilitiesService inner) => _inner = inner;

        public Task<CapabilitiesResponse> GetCapabilitiesAsync(CapabilitiesRequest request, CancellationToken ct)
            => Task.FromResult(_inner.GetCapabilities(request));
    }
}
