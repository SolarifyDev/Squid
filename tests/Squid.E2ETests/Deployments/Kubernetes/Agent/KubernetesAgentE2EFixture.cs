using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Autofac;
using Halibut;
using Microsoft.Extensions.Configuration;
using Serilog;
using Squid.Core.Settings.GithubPackage;
using Squid.Core.Settings.Halibut;
using Squid.Core.Settings.SelfCert;
using Squid.E2ETests.Infrastructure;

namespace Squid.E2ETests.Deployments.Kubernetes.Agent;

public class KubernetesAgentE2EFixture<TTestClass> : E2EFixtureBase<TTestClass>
{
    public TentacleStub Stub { get; private set; }
    public CapturingLogSink LogSink { get; } = new();

    private int _pollingPort;
    private string _calamariCacheDir;

    protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
    {
        _pollingPort = GetAvailablePort();

        builder.RegisterInstance(new PollingListenerSetting
        {
            Enabled = true,
            Port = _pollingPort
        }).AsSelf().SingleInstance();

        SetupCalamariCache(builder, configuration);
    }

    protected override Task OnInitializedAsync()
    {
        CreateDummyCalamariPackage();

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

        if (Directory.Exists(_calamariCacheDir))
            Directory.Delete(_calamariCacheDir, recursive: true);
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

    private void SetupCalamariCache(ContainerBuilder builder, IConfiguration configuration)
    {
        _calamariCacheDir = Path.Combine(Path.GetTempPath(), $"squid-e2e-calamari-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_calamariCacheDir);

        configuration["CalamariGithubPackage:Version"] = "0.0.1-test";
        configuration["CalamariGithubPackage:CacheDirectory"] = _calamariCacheDir;

        builder.RegisterInstance(new CalamariGithubPackageSetting
        {
            Version = "0.0.1-test",
            CacheDirectory = _calamariCacheDir,
            Token = string.Empty,
            MirrorUrlTemplate = string.Empty
        }).AsSelf().SingleInstance();
    }

    private void CreateDummyCalamariPackage()
    {
        var packagePath = Path.Combine(_calamariCacheDir, "Calamari.0.0.1-test.nupkg");

        using var stream = File.Create(packagePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var nuspecEntry = archive.CreateEntry("Calamari.nuspec");
        using var writer = new StreamWriter(nuspecEntry.Open());
        writer.Write("""
            <?xml version="1.0" encoding="utf-8"?>
            <package>
              <metadata>
                <id>Calamari</id>
                <version>0.0.1-test</version>
                <description>Dummy Calamari for E2E tests</description>
              </metadata>
            </package>
            """);
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
