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

        builder.RegisterInstance(new PollingListenerSetting
        {
            Enabled = true,
            Port = _pollingPort
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

        var kubeconfigPath = Environment.GetEnvironmentVariable("SQUID_E2E_KUBECONFIG")
                             ?? KindClusterFixture.DefaultKubeconfigPath;

        Stub = new TentacleStub(serverThumbprint, _pollingPort, kubeconfigPath);

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

#pragma warning disable SYSLIB0057
        using var cert = new X509Certificate2(certBytes, selfCertSetting.Password);
#pragma warning restore SYSLIB0057

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
