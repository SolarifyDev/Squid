using System.Net;
using System.Net.Sockets;
using Autofac;
using Halibut;
using Microsoft.Extensions.Configuration;
using Serilog;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Machines;
using Squid.Core.Settings.Halibut;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Machine;
using Squid.Tentacle.Certificate;

namespace Squid.E2ETests.Deployments.Tentacle;

/// <summary>
/// E2E fixture for Tentacle in Polling mode.
/// Creates a TentacleStub that polls the Server's Halibut listener, registers via
/// <c>/api/machines/register/tentacle-polling</c>, then executes real deployments.
/// </summary>
public class TentaclePollingE2EFixture<TTestClass> : E2EFixtureBase<TTestClass>
{
    public CapturingLogSink LogSink { get; } = new();
    public int TentacleMachineId { get; private set; }
    public string TentacleSubscriptionId { get; private set; }
    public string TentacleThumbprint { get; private set; }
    public int EnvironmentId { get; private set; }
    public string EnvironmentName { get; private set; }

    private TentacleStub _stub;
    private int _pollingPort;

    protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
    {
        _pollingPort = GetAvailablePort();

        builder.RegisterInstance(new HalibutSetting
        {
            Polling = new PollingSettings { Enabled = true, Port = _pollingPort }
        }).AsSelf().SingleInstance();
    }

    protected override async Task OnInitializedAsync()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.Sink(LogSink)
            .CreateLogger();

        await CreateEnvironmentAsync().ConfigureAwait(false);
        await RegisterTentacleAsync().ConfigureAwait(false);
        StartPollingStub();
    }

    protected override async Task OnDisposingAsync()
    {
        if (_stub != null)
            await _stub.DisposeAsync().ConfigureAwait(false);
    }

    private async Task CreateEnvironmentAsync()
    {
        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var builder = new TestDataBuilder(repo, uow);
            var env = await builder.CreateEnvironmentAsync("Tentacle Polling E2E Env").ConfigureAwait(false);

            EnvironmentId = env.Id;
            EnvironmentName = env.Name;
        }).ConfigureAwait(false);
    }

    private async Task RegisterTentacleAsync()
    {
        var certsPath = Path.Combine(Path.GetTempPath(), $"squid-tentacle-polling-certs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(certsPath);

        var certManager = new TentacleCertificateManager(certsPath);
        var tentacleCert = certManager.LoadOrCreateCertificate();
        TentacleSubscriptionId = certManager.LoadOrCreateSubscriptionId();
        TentacleThumbprint = tentacleCert.Thumbprint;

        var registration = await Run<IMachineRegistrationService, RegisterMachineResponseData>(async svc =>
        {
            return await svc.RegisterTentaclePollingAsync(new RegisterTentaclePollingCommand
            {
                MachineName = $"tentacle-polling-{TentacleSubscriptionId[..8]}",
                Thumbprint = TentacleThumbprint,
                SubscriptionId = TentacleSubscriptionId,
                Roles = "linux-server",
                Environments = EnvironmentName,
                AgentVersion = "1.0.0-test"
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        TentacleMachineId = registration.MachineId;

        Log.Information("Tentacle Polling registered. MachineId={MachineId}, SubscriptionId={SubscriptionId}",
            TentacleMachineId, TentacleSubscriptionId);
    }

    private void StartPollingStub()
    {
        var serverThumbprint = GetServerThumbprint();

        _stub = TentacleStub.CreatePolling(serverThumbprint, _pollingPort);

        // The stub generates its own cert. We need to re-register with the stub's thumbprint
        // so Halibut trust matches. But since we already registered above with the certManager's
        // thumbprint, we trust the certManager's thumbprint on the server side.
        // Instead, trust the stub's cert separately:
        var halibutRuntime = LifetimeScope.Resolve<HalibutRuntime>();
        halibutRuntime.Trust(_stub.Thumbprint);

        // Re-register with stub's actual thumbprint + subscriptionId
        Run<IMachineRegistrationService>(async svc =>
        {
            await svc.RegisterTentaclePollingAsync(new RegisterTentaclePollingCommand
            {
                MachineName = $"tentacle-polling-{_stub.SubscriptionId[..8]}",
                Thumbprint = _stub.Thumbprint,
                SubscriptionId = _stub.SubscriptionId,
                Roles = "linux-server",
                Environments = EnvironmentName,
                AgentVersion = "1.0.0-test"
            }).ConfigureAwait(false);
        }).GetAwaiter().GetResult();

        TentacleSubscriptionId = _stub.SubscriptionId;
        TentacleThumbprint = _stub.Thumbprint;

        Log.Information("Tentacle Polling Stub started. Port={Port}, SubscriptionId={SubscriptionId}",
            _pollingPort, TentacleSubscriptionId);
    }

    private string GetServerThumbprint()
    {
        var selfCertSetting = LifetimeScope.Resolve<Core.Settings.SelfCert.SelfCertSetting>();
        var certBytes = Convert.FromBase64String(selfCertSetting.Base64);
        using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            certBytes, selfCertSetting.Password);

        return cert.Thumbprint;
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}
