using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Autofac;
using Halibut;
using Microsoft.Extensions.Configuration;
using Serilog;
using Squid.Core.Settings.Halibut;
using Squid.Core.Settings.SelfCert;
using Squid.E2ETests.Infrastructure;

namespace Squid.E2ETests.Deployments.Kubernetes.Agent;

public class KubernetesAgentE2EFixture<TTestClass> : E2EFixtureBase<TTestClass>
{
    public TentacleStub Stub { get; private set; }
    public CapturingLogSink LogSink { get; } = new();

    private int _pollingPort;
    protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
    {
        _pollingPort = GetAvailablePort();

        builder.RegisterInstance(new HalibutSetting
        {
            Polling = new PollingSettings { Enabled = true, Port = _pollingPort }
        }).AsSelf().SingleInstance();
    }

    protected override Task OnInitializedAsync()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.Sink(LogSink)
            .CreateLogger();

        var serverThumbprint = GetServerThumbprint();

        Stub = TentacleStub.CreatePolling(serverThumbprint, _pollingPort, KindClusterFixture.DefaultKubeconfigPath);

        var halibutRuntime = LifetimeScope.Resolve<HalibutRuntime>();
        halibutRuntime.Trust(Stub.Thumbprint);

        return Task.CompletedTask;
    }

    protected override async Task OnDisposingAsync()
    {
        if (Stub != null)
            await Stub.DisposeAsync().ConfigureAwait(false);
    }

    private string GetServerThumbprint()
    {
        var selfCertSetting = LifetimeScope.Resolve<SelfCertSetting>();
        var certBytes = Convert.FromBase64String(selfCertSetting.Base64);

        using var cert = X509CertificateLoader.LoadPkcs12(certBytes, selfCertSetting.Password);

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
