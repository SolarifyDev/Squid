using System.Reflection;
using Squid.Core.Caching;
using Squid.Core.Halibut;

namespace Squid.Core;

public class SquidModule : Module
{
    private readonly ILogger _logger;
    private readonly IConfiguration _configuration;
    private readonly SquidStoreSetting _storeSetting;
    private readonly Assembly[] _assemblies;

    public SquidModule(
        ILogger logger,
        IConfiguration configuration,
        SquidStoreSetting storeSetting)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _storeSetting = storeSetting ?? throw new ArgumentNullException(nameof(storeSetting));
        _assemblies = new[] { typeof(SquidModule).Assembly };
    }

    protected override void Load(ContainerBuilder builder)
    {
        RegisterLogging(builder);
        RegisterSettings(builder);
        RegisterMediator(builder);
        RegisterPersistence(builder);
        RegisterHalibut(builder);
        RegisterCaching(builder);
        RegisterAutoMapper(builder);
        RegisterDependency(builder);
    }

    private void RegisterLogging(ContainerBuilder builder)
    {
        builder.RegisterModule(new LoggingModule(_logger));
    }

    private void RegisterSettings(ContainerBuilder builder)
    {
        builder.RegisterModule(new SettingModule(_configuration, _assemblies));
    }

    private void RegisterMediator(ContainerBuilder builder)
    {
        builder.RegisterModule(new MediatorModule(_assemblies));
    }

    private void RegisterPersistence(ContainerBuilder builder)
    {
        builder.RegisterModule(new PersistenceModule(_storeSetting, _logger));
    }

    private void RegisterHalibut(ContainerBuilder builder)
    {
        builder.RegisterModule(new HalibutModule());
    }

    private void RegisterCaching(ContainerBuilder builder)
    {
        builder.RegisterModule(new CachingModule());
    }

    private void RegisterAutoMapper(ContainerBuilder builder)
    {
        builder.RegisterAutoMapper(assemblies: _assemblies);

        builder.RegisterBuildCallback(container =>
        {
            var mapper = container.Resolve<IMapper>();
            AutoMapperConfiguration.Init(mapper.ConfigurationProvider);
        });
    }

    private void RegisterDependency(ContainerBuilder builder)
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
