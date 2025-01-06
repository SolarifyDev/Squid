using AutoMapper;
using Squid.Core.Mappings;
using Squid.Infrastructure.Authentication;
using Squid.Infrastructure.Logging;
using Squid.Infrastructure.Mediation;
using Squid.Infrastructure.Settings;

namespace Squid.Infrastructure;

public class ApplicationStartup
{
    public static void Initialize(
        ContainerBuilder builder,
        SquidStoreSetting storeSetting,
        ILogger logger,
        IUserContext userContext,
        IConfiguration configuration)
    {
        var assemblies = new[] { typeof(IUserContext).Assembly, typeof(ApplicationStartup).Assembly };

        builder.RegisterModule(new LoggingModule(logger));
        builder.RegisterModule(new AuthenticationModule(userContext));
        builder.RegisterModule(new SettingModule(configuration, assemblies));
        builder.RegisterModule(new MediatorModule(assemblies));
        builder.RegisterModule(new PersistenceModule(storeSetting));
        builder.RegisterAutoMapper(assemblies: assemblies);
        
        RegisterDependency(builder);
        
        builder.RegisterBuildCallback(container =>
        {
            var mapper = container.Resolve<IMapper>();
            AutoMapperConfiguration.Init(mapper.ConfigurationProvider);
        });

        InitializeDatabase(logger, storeSetting);
    }

    private static void RegisterDependency(ContainerBuilder builder)
    {
        foreach (var type in typeof(IDependency).Assembly.GetTypes()
                     .Where(type => type.IsClass && typeof(IDependency).IsAssignableFrom(type)))
        {
            if (typeof(IScopedDependency).IsAssignableFrom(type))
                builder.RegisterType(type).AsSelf().AsImplementedInterfaces().InstancePerLifetimeScope();
            else if (typeof(ISingletonDependency).IsAssignableFrom(type))
                builder.RegisterType(type).AsSelf().AsImplementedInterfaces().SingleInstance();
            else if (typeof(ITransientDependency).IsAssignableFrom(type))
                builder.RegisterType(type).AsSelf().AsImplementedInterfaces().InstancePerDependency();
            else
                builder.RegisterType(type).AsSelf().AsImplementedInterfaces();
        }
    }

    private static void InitializeDatabase(ILogger logger, SquidStoreSetting storeSetting)
    {
        switch (storeSetting.Type)
        {
            case SquidStoreSetting.SquidStoreType.Postgres:
                var postgresDbUp = new PostgresDbUp(storeSetting.Postgres!.ConnectionString,
                    new DbUpLogger<PostgresDbUp>(logger));
                postgresDbUp.Run();
                break;
            case SquidStoreSetting.SquidStoreType.Volatile:
            case SquidStoreSetting.SquidStoreType.MySql:
            default:
                break;
        }
    }
}