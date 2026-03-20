using Squid.Core.Caching;
using Squid.Core.Halibut;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Identity;
using Squid.Core.Services.Spaces;
using Squid.Core.Settings.System;

namespace Squid.Core;

public class SquidModule : Module
{
    private readonly ILogger _logger;
    private readonly IConfiguration _configuration;
    private readonly Assembly[] _assemblies;

    public SquidModule(ILogger logger, IConfiguration configuration)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
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

        builder.RegisterType<BuiltInRoleSeeder>().As<IStartable>().SingleInstance();
        builder.RegisterType<DefaultSpaceSeeder>().As<IStartable>().SingleInstance();
    }

    private void RegisterLogging(ContainerBuilder builder)
    {
        builder.RegisterModule(new LoggingModule(_logger));
    }

    private void RegisterSettings(ContainerBuilder builder)
    {
        var settingTypes = typeof(SquidModule).Assembly.GetTypes()
            .Where(t => t.IsClass && typeof(IConfigurationSetting).IsAssignableFrom(t))
            .ToArray();

        builder.RegisterTypes(settingTypes).AsSelf().SingleInstance();
    }

    private void RegisterMediator(ContainerBuilder builder)
    {
        builder.RegisterModule(new MediatorModule(_assemblies));
    }

    private void RegisterPersistence(ContainerBuilder builder)
    {
        var connectionString = new SquidConnectionString(_configuration).Value;

        builder.Register(_ =>
            {
                var optionsBuilder = new DbContextOptionsBuilder<SquidDbContext>();

                optionsBuilder
                    .UseNpgsql(connectionString)
                    .UseSnakeCaseNamingConvention();

                return optionsBuilder.Options;
            })
            .As<DbContextOptions<SquidDbContext>>()
            .SingleInstance();

        builder.Register(c =>
            {
                var options = c.Resolve<DbContextOptions<SquidDbContext>>();
                var currentUser = c.ResolveOptional<ICurrentUser>();
                return new SquidDbContext(options, currentUser);
            })
            .AsSelf()
            .As<DbContext>()
            .AsImplementedInterfaces()
            .InstancePerLifetimeScope();

        builder.RegisterType<EfRepository>().As<IRepository>().InstancePerLifetimeScope();
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
                     .Where(type => type.IsClass && !type.IsAbstract && typeof(IDependency).IsAssignableFrom(type)))
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
