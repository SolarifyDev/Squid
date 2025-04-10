using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Autofac;
using Halibut;
using Halibut.Diagnostics;
using Halibut.ServiceModel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Squid.Api;
using Squid.Infrastructure.Halibut;

namespace Squid.E2ETests.Halibut;

public class HalibutApiTestFixture : WebApplicationFactory<Startup>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureContainer<ContainerBuilder>(b =>
        {
            RegisterHalibut(b);
        });
        return base.CreateHost(builder);
    }

    private void RegisterHalibut(ContainerBuilder containerBuilder)
    {
        containerBuilder.Register(c =>
        {
            var services = new DelegateServiceFactory();
            var serverCert = new X509Certificate2("../../../Halibut/squid.pfx", "squid123");

            var halibutTimeoutsAndLimits = HalibutTimeoutsAndLimits.RecommendedValues();

            var halibutRuntime = new HalibutRuntimeBuilder()
                .WithServiceFactory(services)
                .WithServerCertificate(serverCert)
                .WithHalibutTimeoutsAndLimits(halibutTimeoutsAndLimits)
                .Build();

            return halibutRuntime;
        }).Keyed<HalibutRuntime>("TestHalibutRuntime").SingleInstance();
    }

    public override async ValueTask DisposeAsync()
    {
        await ClearDatabaseRecord();
        
        await base.DisposeAsync();
    }
    
    private async Task ClearDatabaseRecord()
    {
        await Services.GetRequiredService<DbContext>().Database.EnsureDeletedAsync();
    }
}