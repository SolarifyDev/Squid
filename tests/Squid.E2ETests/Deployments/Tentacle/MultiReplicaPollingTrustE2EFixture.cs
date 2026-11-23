using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Autofac;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Serilog;
using Squid.Core;
using Squid.Core.Services.Jobs;
using Squid.Core.Settings.Halibut;
using Squid.Core.Settings.SelfCert;
using Squid.E2ETests.Infrastructure;

namespace Squid.E2ETests.Deployments.Tentacle;

/// <summary>
/// Two real Squid server replicas over one database. Replica A is the fixture's own container; replica B is a second full
/// <see cref="SquidModule"/> container (own certificate, own Halibut polling listener, own trust list) built against the same database.
/// B's periodic trust refresh is set to an hour, so anything B learns inside a test it learned through the unauthorized-connect trigger,
/// not the timer.
/// </summary>
public class MultiReplicaPollingTrustE2EFixture<TTestClass> : E2EFixtureBase<TTestClass>
{
    public ILifetimeScope ReplicaB { get; private set; }
    public int ReplicaBPollingPort { get; private set; }
    public string ReplicaBThumbprint { get; private set; }

    protected override void RegisterOverrides(ContainerBuilder builder, IConfiguration configuration)
    {
        builder.RegisterInstance(new HalibutSetting { Polling = new PollingSettings { Enabled = true, Port = GetAvailablePort() } }).AsSelf().SingleInstance();
    }

    protected override Task OnInitializedAsync()
    {
        var certBytes = CreatePfx();

        ReplicaBPollingPort = GetAvailablePort();
        ReplicaBThumbprint = X509CertificateLoader.LoadPkcs12(certBytes, string.Empty).Thumbprint;

        var configuration = LifetimeScope.Resolve<IConfiguration>();
        var builder = new ContainerBuilder();

        builder.RegisterInstance(configuration).As<IConfiguration>().SingleInstance();
        builder.RegisterModule(new SquidModule(Log.Logger, configuration));
        builder.RegisterInstance(new SelfCertSetting { Base64 = Convert.ToBase64String(certBytes), Password = string.Empty }).AsSelf().SingleInstance();
        builder.RegisterInstance(BuildReplicaBHalibutSetting()).AsSelf().SingleInstance();
        builder.RegisterType<NoOpBackgroundJobClient>().As<ISquidBackgroundJobClient>().InstancePerLifetimeScope();
        builder.Register(_ => new MemoryCache(new MemoryCacheOptions())).As<IMemoryCache>().SingleInstance();

        ReplicaB = builder.Build();

        return Task.CompletedTask;
    }

    protected override Task OnDisposingAsync()
    {
        (ReplicaB as IDisposable)?.Dispose();

        return Task.CompletedTask;
    }

    private HalibutSetting BuildReplicaBHalibutSetting() => new()
    {
        Polling = new PollingSettings { Enabled = true, Port = ReplicaBPollingPort },
        Trust = new TrustSettings { RefreshIntervalSeconds = 3600 }
    };

    private static byte[] CreatePfx()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=squid-e2e-replica-b", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));

        return cert.Export(X509ContentType.Pfx, string.Empty);
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
