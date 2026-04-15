using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Autofac;
using Halibut;
using Microsoft.Extensions.Configuration;
using Serilog;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Machines;
using Squid.Core.Settings.Halibut;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Commands.Machine;
using Squid.Tentacle.Certificate;

namespace Squid.E2ETests.Deployments.Tentacle;

/// <summary>
/// E2E fixture with BOTH a Polling and a Listening TentacleStub.
/// Tests that a single deployment can target both communication styles simultaneously.
/// </summary>
public class TentacleMixedModeE2EFixture<TTestClass> : E2EFixtureBase<TTestClass>
{
    public CapturingLogSink LogSink { get; } = new();
    public int EnvironmentId { get; private set; }
    public string EnvironmentName { get; private set; }

    // Polling stub
    public int PollingMachineId { get; private set; }
    public string PollingSubscriptionId { get; private set; }
    public string PollingThumbprint { get; private set; }

    // Listening stub
    public int ListeningMachineId { get; private set; }
    public string ListeningThumbprint { get; private set; }
    public int ListeningPort { get; private set; }

    private TentacleStub _pollingStub;
    private TentacleStub _listeningStub;
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
        StartPollingStub();
        StartListeningStub();
        await RegisterPollingMachineAsync().ConfigureAwait(false);
        await InsertListeningMachineAsync().ConfigureAwait(false);
    }

    protected override async Task OnDisposingAsync()
    {
        if (_pollingStub != null) await _pollingStub.DisposeAsync().ConfigureAwait(false);
        if (_listeningStub != null) await _listeningStub.DisposeAsync().ConfigureAwait(false);
    }

    private async Task CreateEnvironmentAsync()
    {
        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var builder = new TestDataBuilder(repo, uow);
            var env = await builder.CreateEnvironmentAsync("Tentacle Mixed Mode E2E Env").ConfigureAwait(false);

            EnvironmentId = env.Id;
            EnvironmentName = env.Name;
        }).ConfigureAwait(false);
    }

    private void StartPollingStub()
    {
        var serverThumbprint = GetServerThumbprint();
        _pollingStub = TentacleStub.CreatePolling(serverThumbprint, _pollingPort);
        PollingThumbprint = _pollingStub.Thumbprint;
        PollingSubscriptionId = _pollingStub.SubscriptionId;

        var halibutRuntime = LifetimeScope.Resolve<HalibutRuntime>();
        halibutRuntime.Trust(_pollingStub.Thumbprint);
    }

    private void StartListeningStub()
    {
        ListeningPort = GetAvailablePort();
        _listeningStub = TentacleStub.CreateListening(ListeningPort);
        ListeningThumbprint = _listeningStub.Thumbprint;

        var serverThumbprint = GetServerThumbprint();
        _listeningStub.Trust(serverThumbprint);
    }

    private async Task RegisterPollingMachineAsync()
    {
        var registration = await Run<IMachineRegistrationService, RegisterMachineResponseData>(async svc =>
        {
            return await svc.RegisterTentaclePollingAsync(new RegisterTentaclePollingCommand
            {
                MachineName = $"mixed-polling-{PollingSubscriptionId[..8]}",
                Thumbprint = PollingThumbprint,
                SubscriptionId = PollingSubscriptionId,
                Roles = "linux-server",
                Environments = EnvironmentName,
                AgentVersion = "1.0.0-test"
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        PollingMachineId = registration.MachineId;
    }

    private async Task InsertListeningMachineAsync()
    {
        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var endpointJson = JsonSerializer.Serialize(new
            {
                CommunicationStyle = "TentacleListening",
                Uri = $"https://localhost:{ListeningPort}/",
                Thumbprint = ListeningThumbprint
            });

            var machine = new Machine
            {
                Name = $"mixed-listening-{Guid.NewGuid().ToString("N")[..8]}",
                IsDisabled = false,
                Roles = "[\"linux-server\"]",
                EnvironmentIds = $"[{EnvironmentId}]",
                Endpoint = endpointJson,
                SpaceId = 1,
                Slug = $"mixed-listening-{Guid.NewGuid():N}"
            };

            await repo.InsertAsync(machine).ConfigureAwait(false);
            await uow.SaveChangesAsync().ConfigureAwait(false);

            ListeningMachineId = machine.Id;
        }).ConfigureAwait(false);
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
