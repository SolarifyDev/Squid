using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Squid.Core.Halibut.RedisQueue;
using Squid.Core.Settings.Halibut;
using Squid.Core.Settings.SelfCert;
using Squid.Message.Hardening;

namespace Squid.Core.Halibut;

public class HalibutModule : Module
{
    /// <summary>
    /// P1-E.10 (Phase-8): env var overriding the TCP receive-response
    /// timeout (Halibut waits this long for an agent's reply on a polling
    /// dispatch). Default <see cref="HalibutTimeoutsAndLimits.RecommendedValues"/>
    /// is 10 minutes — fine for normal scripts but overkill for short
    /// health checks and (per E.1) blocks observer cancel-propagation
    /// on stuck agents. Air-gapped operators sometimes need to LOWER this
    /// to fail fast on lost connectivity.
    ///
    /// <para>Recognised values: positive integer SECONDS. Zero / negative /
    /// blank / unrecognised → fall back to RecommendedValues default.
    /// Pinned literal — renaming breaks operators who already set the
    /// env var via deployment config.</para>
    /// </summary>
    public const string TcpReceiveResponseTimeoutSecondsEnvVar = "SQUID_HALIBUT_TCP_RECEIVE_RESPONSE_TIMEOUT_SECONDS";

    /// <summary>
    /// Env var overriding <c>PollingRequestQueueTimeout</c>: how long a request the SERVER dispatches to a polling agent may wait
    /// in the server's queue for that agent to connect and pick it up. When it elapses the dispatch fails with a transient
    /// timeout (it is NOT how long the agent waits before re-polling, and lowering it does not reduce idle latency). Lower it
    /// and a busy or briefly offline agent fails dispatches (and health probes, cancels) sooner; raise it and they wait longer.
    /// Health-check probe timeouts and the Cancel retry budget should stay above/around it
    /// (see <c>MachineHealthCheckSetting</c> and <c>RpcRetrySettings</c>). Positive integer seconds; anything else keeps the default.
    /// </summary>
    public const string PollingRequestQueueTimeoutSecondsEnvVar = "SQUID_HALIBUT_POLLING_QUEUE_TIMEOUT_SECONDS";

    protected override void Load(ContainerBuilder builder)
    {
        // Connection-log factory: expose the runtime's OWN per-endpoint connection
        // log (Halibut builds a bounded in-memory recent-events LogFactory by default
        // and writes every connection event to it — OpeningNewConnection,
        // SecurityNegotiation, MessageExchange, Error, ...). We surface that built-in
        // factory as ILogFactory so the connection-log reader shares the exact
        // instance the runtime uses (no parallel factory to keep in sync) and serves
        // GET /api/machine/{id}/connection-log. Mirrors Octopus's CommunicationLogFactory,
        // which reads halibut.Logs directly.
        builder.Register(ctx => ctx.Resolve<HalibutRuntime>().Logs).As<ILogFactory>().SingleInstance();

        // Published-identity enforcement runs here, synchronously at container build, NOT in
        // the runtime factory below: the factory is resolved lazily and the startup path that
        // touches it swallows exceptions into a log line, which left a strict-mode server
        // half-alive (UI up, every deploy/health-check failing at DI resolution).
        builder.RegisterType<SelfCertStartupCheck>().As<IStartable>().SingleInstance();

        builder.Register(ctx =>
        {
            var selfCertSetting = ctx.Resolve<SelfCertSetting>();

            // Fail with an actionable message before the loader turns a missing identity
            // into an opaque FormatException.
            SelfCertValidator.EnsureConfigured(selfCertSetting.Base64);

            var certBytes = Convert.FromBase64String(selfCertSetting.Base64);
            var serverCert = X509CertificateLoader.LoadPkcs12(certBytes, selfCertSetting.Password, X509KeyStorageFlags.MachineKeySet);

            var services = new DelegateServiceFactory();

            var halibutTimeoutsAndLimits = BuildTimeoutsAndLimits();

            LogTimeoutOverrides(halibutTimeoutsAndLimits);

            var refresher = ctx.Resolve<IPollingTrustRefresher>();

            // Polling-agent requests queue locally first and cross pods through Redis only when no poller of the agent is attached here (see FailoverPendingRequestQueue).
            var halibutRuntime = new HalibutRuntimeBuilder()
                .WithServiceFactory(services)
                .WithTrustProvider(ctx.Resolve<SnapshotTrustProvider>())
                .WithOnUnauthorizedClientConnect((_, thumbprint) => BlockAndRefreshTrust(refresher, thumbprint))
                .WithServerCertificate(serverCert)
                .WithHalibutTimeoutsAndLimits(halibutTimeoutsAndLimits)
                .WithFailoverQueue(ctx, halibutTimeoutsAndLimits, new LogFactory())
                .Build();

            Log.Information("HalibutRuntime created. ServerCertThumbprint={Thumbprint}", serverCert.Thumbprint);

            StartPollingListenerIfEnabled(ctx, halibutRuntime);

            return halibutRuntime;

        }).As<HalibutRuntime>().SingleInstance();

        builder.RegisterType<SnapshotTrustProvider>().AsSelf().SingleInstance();

        builder.RegisterType<PollingTrustRefresher>().As<IPollingTrustRefresher>().As<IStartable>().SingleInstance();

        HalibutQueueWiring.RegisterComponents(builder);

        builder.RegisterType<PollingTrustDistributor>().As<IPollingTrustDistributor>().As<IStartable>().SingleInstance();
    }

    // An unknown client is still rejected (this callback must stay synchronous, so it cannot wait for the database); the refresh it
    // triggers makes the agent's next reconnect succeed if it was registered through another replica.
    private static UnauthorizedClientConnectResponse BlockAndRefreshTrust(IPollingTrustRefresher refresher, string thumbprint)
    {
        refresher.OnUnauthorizedClient(thumbprint);

        return UnauthorizedClientConnectResponse.BlockConnection;
    }

    /// <summary>
    /// SSOT for <see cref="HalibutTimeoutsAndLimits"/>: starts from
    /// <see cref="HalibutTimeoutsAndLimits.RecommendedValues"/> and applies the env-var
    /// overrides. Used for the <see cref="HalibutRuntime"/> AND for every
    /// <see cref="ServiceEndPoint"/> the server builds (see EndpointJsonHelper) —
    /// Halibut copies PollingRequestQueueTimeout, RetryListeningSleepInterval,
    /// RetryCountLimit, ConnectionErrorRetryTimeout and TcpClientConnectTimeout onto the
    /// endpoint at construction and reads them from <c>request.Destination</c> at dispatch
    /// (PendingRequestQueueAsync), so an override applied only to the runtime never
    /// reaches a polling dispatch. Pure and quiet: safe to call per endpoint.
    /// </summary>
    internal static HalibutTimeoutsAndLimits BuildTimeoutsAndLimits()
    {
        var values = HalibutTimeoutsAndLimits.RecommendedValues();

        if (TryReadSecondsEnv(TcpReceiveResponseTimeoutSecondsEnvVar, out var tcpReceive))
            values.TcpClientReceiveResponseTimeout = tcpReceive;

        if (TryReadSecondsEnv(PollingRequestQueueTimeoutSecondsEnvVar, out var pollingQueue))
            values.PollingRequestQueueTimeout = pollingQueue;

        return values;
    }

    private static void LogTimeoutOverrides(HalibutTimeoutsAndLimits values)
    {
        Log.Information(
            "Halibut effective limits: PollingRequestQueueTimeout={PollingQueueSeconds}s (override {PollingQueueEnvVar}), TcpClientReceiveResponseTimeout={ReceiveSeconds}s (override {ReceiveEnvVar}), TcpClientConnectTimeout={ConnectSeconds}s, RetryCountLimit={RetryCount}, ConnectionErrorRetryTimeout={ConnectionRetrySeconds}s",
            values.PollingRequestQueueTimeout.TotalSeconds, PollingRequestQueueTimeoutSecondsEnvVar, values.TcpClientReceiveResponseTimeout.TotalSeconds, TcpReceiveResponseTimeoutSecondsEnvVar,
            values.TcpClientConnectTimeout.TotalSeconds, values.RetryCountLimit, values.ConnectionErrorRetryTimeout.TotalSeconds);
    }

    /// <summary>
    /// Pure parser for an integer-seconds env var. Exposed internal for
    /// unit testing the matrix without touching real env state.
    /// </summary>
    internal static bool TryParseSecondsEnv(string raw, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!int.TryParse(raw.Trim(), out var seconds)) return false;
        if (seconds <= 0) return false;
        value = TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static bool TryReadSecondsEnv(string envName, out TimeSpan value)
        => TryParseSecondsEnv(System.Environment.GetEnvironmentVariable(envName), out value);

    private static void StartPollingListenerIfEnabled(IComponentContext ctx, HalibutRuntime halibutRuntime)
    {
        if (!ctx.TryResolve<HalibutSetting>(out var halibutSetting))
        {
            Log.Warning("HalibutSetting not found in configuration. Polling listener will NOT start");
            return;
        }

        if (!halibutSetting.Polling.Enabled)
        {
            Log.Information("Halibut polling is disabled (Halibut:Polling:Enabled=false). Agents cannot connect via polling");
            return;
        }

        var port = halibutSetting.Polling.Port;

        halibutRuntime.Listen(port);

        Log.Information("Halibut polling listener started on port {Port}", port);
    }
}
