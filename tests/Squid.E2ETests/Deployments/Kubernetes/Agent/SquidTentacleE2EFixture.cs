using System.Net;
using System.Net.Sockets;
using Autofac;
using Halibut;
using Microsoft.Extensions.Configuration;
using Serilog;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Halibut;
using Squid.Tentacle.ScriptExecution;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Machines;
using Squid.Core.Settings.Halibut;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Machine;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.E2ETests.Deployments.Kubernetes.Agent;

/// <summary>
/// E2E fixture that uses real Squid.Tentacle components (TentacleCertificateManager, TentacleHalibutHost, LocalScriptService)
/// instead of TentacleStub. Tests the full registration → polling → execution flow.
/// </summary>
public class SquidTentacleE2EFixture<TTestClass> : E2EFixtureBase<TTestClass>
{
    public CapturingLogSink LogSink { get; } = new();
    public int TentacleMachineId { get; private set; }
    public string TentacleSubscriptionId { get; private set; }
    public string TentacleThumbprint { get; private set; }
    public int TentacleEnvironmentId { get; private set; }

    private TentacleHalibutHost _tentacleHost;
    private int _pollingPort;
    private string _certsPath;
    protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
    {
        _pollingPort = GetAvailablePort();

        builder.RegisterInstance(new PollingListenerSetting
        {
            Enabled = true,
            Port = _pollingPort
        }).AsSelf().SingleInstance();
    }

    protected override async Task OnInitializedAsync()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.Sink(LogSink)
            .CreateLogger();

        SetKubeconfigEnvironment();
        await CreateEnvironmentAsync().ConfigureAwait(false);
        await RegisterRealTentacleAsync().ConfigureAwait(false);
        StartRealTentaclePolling();
    }

    protected override async Task OnDisposingAsync()
    {
        if (_tentacleHost != null)
            await _tentacleHost.DisposeAsync().ConfigureAwait(false);

        if (Directory.Exists(_certsPath))
            Directory.Delete(_certsPath, recursive: true);
    }

    // ========================================================================
    // Initialization Steps
    // ========================================================================

    private static void SetKubeconfigEnvironment()
    {
        var kubeconfigPath = System.Environment.GetEnvironmentVariable("SQUID_E2E_KUBECONFIG")
                             ?? KindClusterFixture.DefaultKubeconfigPath;

        System.Environment.SetEnvironmentVariable("KUBECONFIG", kubeconfigPath);
    }

    private async Task CreateEnvironmentAsync()
    {
        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var builder = new TestDataBuilder(repo, uow);
            var env = await builder.CreateEnvironmentAsync("Real Tentacle E2E Environment").ConfigureAwait(false);

            TentacleEnvironmentId = env.Id;
        }).ConfigureAwait(false);
    }

    private async Task RegisterRealTentacleAsync()
    {
        _certsPath = Path.Combine(Path.GetTempPath(), $"squid-tentacle-certs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_certsPath);

        var certManager = new TentacleCertificateManager(_certsPath);
        var tentacleCert = certManager.LoadOrCreateCertificate();
        TentacleSubscriptionId = certManager.LoadOrCreateSubscriptionId();
        TentacleThumbprint = tentacleCert.Thumbprint;

        var registration = await Run<IMachineRegistrationService, RegisterMachineResponseData>(async registrationService =>
        {
            return await registrationService.RegisterMachineAsync(new RegisterMachineCommand
            {
                MachineName = $"squid-tentacle-e2e-{TentacleSubscriptionId[..8]}",
                Thumbprint = tentacleCert.Thumbprint,
                SubscriptionId = TentacleSubscriptionId,
                Roles = "k8s",
                EnvironmentIds = TentacleEnvironmentId.ToString(),
                Namespace = "default"
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        TentacleMachineId = registration.MachineId;

        Log.Information("Real tentacle registered. MachineId={MachineId}, SubscriptionId={SubscriptionId}",
            TentacleMachineId, TentacleSubscriptionId);
    }

    private void StartRealTentaclePolling()
    {
        var certManager = new TentacleCertificateManager(_certsPath);
        var tentacleCert = certManager.LoadOrCreateCertificate();

        var scriptService = new LocalScriptService();
        var settings = new TentacleSettings
        {
            ServerUrl = $"https://localhost:{_pollingPort}",
            ServerPollingPort = _pollingPort
        };

        var serverThumbprint = GetServerThumbprint();

        _tentacleHost = new TentacleHalibutHost(tentacleCert, scriptService, settings);
        _tentacleHost.StartPolling(serverThumbprint, TentacleSubscriptionId);

        Log.Information("Real tentacle polling started on port {Port}", _pollingPort);
    }

    private string GetServerThumbprint()
    {
        var selfCertSetting = LifetimeScope.Resolve<Core.Settings.SelfCert.SelfCertSetting>();
        var certBytes = Convert.FromBase64String(selfCertSetting.Base64);

#pragma warning disable SYSLIB0057
        using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
            certBytes, selfCertSetting.Password);
#pragma warning restore SYSLIB0057

        return cert.Thumbprint;
    }

    // ========================================================================
    // Infrastructure
    // ========================================================================

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}
