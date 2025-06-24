using System.IO;
using Microsoft.Extensions.Configuration;
using Moq;
using Serilog;
using Squid.Message;
using Squid.Core.Settings.SelfCert;

namespace Squid.IntegrationTests;

public class IntegrationFixture : IAsyncLifetime
{
    public readonly ILifetimeScope LifetimeScope;

    public IntegrationFixture()
    {
        var containerBuilder = new ContainerBuilder();
        var logger = new Mock<ILogger>();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", false, true).Build();

        ApplicationStartup.Initialize(
            containerBuilder, 
            configuration.GetSection("SquidStore").Get<SquidStoreSetting>(),
            logger.Object, new IntegrationTestUser(), 
            configuration,
            new SelfCertSetting(configuration));
        
        LifetimeScope = containerBuilder.Build();
    }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        var context = LifetimeScope.Resolve<SquidDbContext>();
        return context.Database.EnsureDeletedAsync();
    }
}