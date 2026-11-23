using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Squid.Api;
using Squid.Api.Extensions;
using Squid.Core;
using Squid.Core.Services.Jobs;
using Squid.Core.Settings.SelfCert;
using Squid.E2ETests.Infrastructure;

namespace Squid.E2ETests.Mcp;

public sealed class McpApiFactory : WebApplicationFactory<Program>
{
    private readonly IConfiguration _configuration;
    private readonly CapturingLogSink _logSink = new();

    public McpApiFactory(IConfiguration configuration)
    {
        _configuration = configuration;
        _logger = new LoggerConfiguration()
            .ConfigureMcpLogging()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(_logSink)
            .CreateLogger();
    }

    private readonly ILogger _logger;

    public CapturingLogSink LogSink => _logSink;

    public void WriteSdkWarningProbe()
    {
        _logger.ForContext("SourceContext", "ModelContextProtocol.Server")
            .Warning("SDK warning probe.");
    }

    protected override IHostBuilder CreateHostBuilder()
    {
        return Host.CreateDefaultBuilder()
            .UseSerilog(_logger, dispose: false)
            .UseServiceProviderFactory(new AutofacServiceProviderFactory())
            .ConfigureContainer<ContainerBuilder>(builder =>
            {
                builder.RegisterModule(new SquidModule(_logger, _configuration));
                builder.RegisterType<NoOpBackgroundJobClient>()
                    .As<ISquidBackgroundJobClient>()
                    .SingleInstance();
                builder.RegisterInstance(CreateSelfCertSetting())
                    .AsSelf()
                    .SingleInstance();
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseContentRoot(AppContext.BaseDirectory);
                webBuilder.UseStartup<Startup>();
                webBuilder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddConfiguration(_configuration));
            });
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(AppContext.BaseDirectory);
        builder.ConfigureTestServices(services => services.AddMemoryCache());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
            (_logger as IDisposable)?.Dispose();
    }

    private static SelfCertSetting CreateSelfCertSetting()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=squid-mcp-e2e",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddYears(1));

        return new SelfCertSetting
        {
            Base64 = Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, string.Empty)),
            Password = string.Empty
        };
    }
}
