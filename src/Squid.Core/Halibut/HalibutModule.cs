using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Squid.Core.Settings.Halibut;
using Squid.Core.Settings.SelfCert;

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
    /// Env var overriding the polling request queue timeout (how long the
    /// agent waits for server-side work before re-polling). Lowering can
    /// reduce idle-agent latency at the cost of more polling RPCs;
    /// raising reduces RPC volume at the cost of stale work pickup.
    /// </summary>
    public const string PollingRequestQueueTimeoutSecondsEnvVar = "SQUID_HALIBUT_POLLING_QUEUE_TIMEOUT_SECONDS";

    protected override void Load(ContainerBuilder builder)
    {
        builder.Register(ctx =>
        {
            var selfCertSetting = ctx.Resolve<SelfCertSetting>();

            var certBytes = Convert.FromBase64String(selfCertSetting.Base64);
            var serverCert = X509CertificateLoader.LoadPkcs12(certBytes, selfCertSetting.Password, X509KeyStorageFlags.MachineKeySet);

            var services = new DelegateServiceFactory();

            var halibutTimeoutsAndLimits = BuildTimeoutsAndLimits();

            var halibutRuntime = new HalibutRuntimeBuilder()
                .WithServiceFactory(services)
                .WithServerCertificate(serverCert)
                .WithHalibutTimeoutsAndLimits(halibutTimeoutsAndLimits)
                .Build();

            Log.Information("HalibutRuntime created. ServerCertThumbprint={Thumbprint}", serverCert.Thumbprint);

            StartPollingListenerIfEnabled(ctx, halibutRuntime);

            return halibutRuntime;

        }).As<HalibutRuntime>().SingleInstance();

        builder.RegisterType<PollingTrustDistributor>().As<IPollingTrustDistributor>().As<IStartable>().SingleInstance();
    }

    /// <summary>
    /// Builds the timeouts/limits with env-var overrides applied. Always
    /// starts from <see cref="HalibutTimeoutsAndLimits.RecommendedValues"/>
    /// — env vars adjust individual fields without rewriting the whole
    /// shape (so when Halibut adds a new field with a sensible default,
    /// we inherit it automatically).
    /// </summary>
    internal static HalibutTimeoutsAndLimits BuildTimeoutsAndLimits()
    {
        var values = HalibutTimeoutsAndLimits.RecommendedValues();

        if (TryReadSecondsEnv(TcpReceiveResponseTimeoutSecondsEnvVar, out var tcpReceive))
        {
            Log.Information("Halibut TcpClientReceiveResponseTimeout overridden via {EnvVar} → {Seconds}s",
                TcpReceiveResponseTimeoutSecondsEnvVar, tcpReceive.TotalSeconds);
            values.TcpClientReceiveResponseTimeout = tcpReceive;
        }

        if (TryReadSecondsEnv(PollingRequestQueueTimeoutSecondsEnvVar, out var pollingQueue))
        {
            Log.Information("Halibut PollingRequestQueueTimeout overridden via {EnvVar} → {Seconds}s",
                PollingRequestQueueTimeoutSecondsEnvVar, pollingQueue.TotalSeconds);
            values.PollingRequestQueueTimeout = pollingQueue;
        }

        return values;
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
