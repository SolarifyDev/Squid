using AutoMapper;
using Squid.Core.Mappings;
using Squid.Core.Settings.SelfCert;
using Squid.Infrastructure.Authentication;
using Squid.Infrastructure.Halibut;
using Squid.Infrastructure.HalibutService;
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
        IConfiguration configuration,
        SelfCertSetting selfCertSetting)
    {
        var assemblies = new[] { typeof(IUserContext).Assembly, typeof(ApplicationStartup).Assembly };

        builder.RegisterModule(new LoggingModule(logger));
        builder.RegisterModule(new AuthenticationModule(userContext));
        builder.RegisterModule(new SettingModule(configuration, assemblies));
        builder.RegisterModule(new MediatorModule(assemblies));
        builder.RegisterModule(new PersistenceModule(storeSetting, logger));
        builder.RegisterModule(new HalibutModule(selfCertSetting));
        builder.RegisterModule(new HalibutServiceModule());
        builder.RegisterAutoMapper(assemblies: assemblies);

        RegisterDependency(builder);

        builder.RegisterBuildCallback(container =>
        {
            var mapper = container.Resolve<IMapper>();
            AutoMapperConfiguration.Init(mapper.ConfigurationProvider);
        });
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
}