using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Autofac;
using Microsoft.Extensions.Configuration;
using Serilog;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Settings.Halibut;
using Squid.E2ETests.Infrastructure;
using Squid.IntegrationTests.Helpers;

namespace Squid.E2ETests.Deployments.Tentacle;

/// <summary>
/// E2E fixture for Linux Tentacle in Listening mode.
/// Creates a TentacleStub that listens on a TCP port. The Server's HalibutRuntime
/// connects outbound to the stub via <c>https://localhost:{port}/</c>.
/// Machine is inserted manually (no agent-initiated registration — Octopus-aligned).
/// </summary>
public class LinuxListeningE2EFixture<TTestClass> : E2EFixtureBase<TTestClass>
{
    public CapturingLogSink LogSink { get; } = new();
    public int TentacleMachineId { get; private set; }
    public string TentacleThumbprint { get; private set; }
    public int ListeningPort { get; private set; }
    public int EnvironmentId { get; private set; }
    public string EnvironmentName { get; private set; }

    private TentacleStub _stub;

    protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
    {
        // Listening mode: Server connects TO agent. We still need the Halibut polling port
        // enabled because HalibutModule creates the HalibutRuntime (used for outbound clients too).
        // The polling listener port doesn't matter for Listening, but HalibutRuntime must exist.
        builder.RegisterInstance(new HalibutSetting
        {
            Polling = new PollingSettings { Enabled = true, Port = GetAvailablePort() }
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
        StartListeningStub();
        await InsertListeningMachineAsync().ConfigureAwait(false);
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
            var env = await builder.CreateEnvironmentAsync("Linux Listening E2E Env").ConfigureAwait(false);

            EnvironmentId = env.Id;
            EnvironmentName = env.Name;
        }).ConfigureAwait(false);
    }

    private void StartListeningStub()
    {
        ListeningPort = GetAvailablePort();

        _stub = TentacleStub.CreateListening(ListeningPort);
        TentacleThumbprint = _stub.Thumbprint;

        // Listening Tentacle must trust the Server's cert (Server is the TLS client,
        // but Halibut uses mutual cert verification)
        var serverThumbprint = GetServerThumbprint();
        _stub.Trust(serverThumbprint);

        Log.Information("Linux Listening TentacleStub started on port {Port}, Thumbprint={Thumbprint}",
            ListeningPort, TentacleThumbprint);
    }

    /// <summary>
    /// Insert machine directly into DB (Listening Tentacles are registered server-side, not by agent).
    /// </summary>
    private async Task InsertListeningMachineAsync()
    {
        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var endpointJson = JsonSerializer.Serialize(new
            {
                CommunicationStyle = "LinuxListening",
                Uri = $"https://localhost:{ListeningPort}/",
                Thumbprint = TentacleThumbprint
            });

            var machine = new Machine
            {
                Name = $"linux-listening-{Guid.NewGuid().ToString("N")[..8]}",
                IsDisabled = false,
                Roles = "[\"linux-server\"]",
                EnvironmentIds = $"[{EnvironmentId}]",
                Endpoint = endpointJson,
                SpaceId = 1,
                Slug = $"linux-listening-{Guid.NewGuid():N}"
            };

            await repo.InsertAsync(machine).ConfigureAwait(false);
            await uow.SaveChangesAsync().ConfigureAwait(false);

            TentacleMachineId = machine.Id;

            Log.Information("Linux Listening machine inserted. MachineId={MachineId}, Uri=https://localhost:{Port}/",
                TentacleMachineId, ListeningPort);
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
