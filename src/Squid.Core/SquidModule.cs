using Squid.Core.Caching;
using Squid.Core.Halibut;
using Squid.Core.Persistence.Db;
using Squid.Core.Services.DataSeeding;
using Squid.Core.Services.Identity;

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

        // MUST come AFTER RegisterDependency — overrides the auto-scanned
        // `ApiUser : ICurrentUser, IScopedDependency` registration so non-HTTP
        // scopes (Hangfire jobs, startup, scheduled tasks) get InternalUser.
        // See P1-D.6 follow-up note.
        RegisterCurrentUser(builder);

        RegisterDataSeeders(builder);
    }

    /// <summary>
    /// P1-D.6 follow-up (Phase-7): the auto-registration in
    /// <see cref="RegisterDependency"/> binds <c>ICurrentUser</c> to
    /// <see cref="ApiUser"/> for every scope (because ApiUser implements
    /// IScopedDependency). After D.6's fail-closed change, ApiUser in a
    /// non-HTTP scope returns null Id → AuthorizationSpecification throws
    /// PermissionDeniedException on any [RequiresPermission] command.
    ///
    /// <para>Hangfire jobs (e.g. <c>MachineHealthCheckRecurringJob</c>
    /// every minute) dispatch <c>AutoMachineHealthCheckCommand</c>
    /// — a permissioned command — through the mediator pipeline. With
    /// auto-only registration this would 100%-of-the-time crash every
    /// minute on production.</para>
    ///
    /// <para>Fix: a context-aware factory chooses ApiUser when HttpContext
    /// exists, InternalUser when it doesn't. The bypass in
    /// AuthorizationSpecification still keys off <c>IsInternal=true</c>
    /// (NOT off Id == 8888), so an ApiUser-with-null-context (DI mishap
    /// mid-request) STILL fails closed — only the type-level InternalUser
    /// signal grants the bypass.</para>
    /// </summary>
    private void RegisterCurrentUser(ContainerBuilder builder)
    {
        builder.Register<ICurrentUser>(c =>
        {
            var accessor = c.ResolveOptional<Microsoft.AspNetCore.Http.IHttpContextAccessor>();

            // Real HTTP request → ApiUser (claim-derived identity).
            if (accessor?.HttpContext != null)
                return c.Resolve<ApiUser>();

            // No HttpContext → background-job / startup / scheduled-task
            // scope. InternalUser carries IsInternal=true so the
            // authorization middleware permits the bypass for known
            // internal contexts.
            return new InternalUser();
        }).InstancePerLifetimeScope();
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

    private static void RegisterDataSeeders(ContainerBuilder builder)
    {
        builder.RegisterType<DataSeederRunner>().As<IStartable>().SingleInstance();

        var seederTypes = typeof(SquidModule).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IDataSeeder).IsAssignableFrom(t))
            .ToArray();

        builder.RegisterTypes(seederTypes).As<IDataSeeder>().SingleInstance();
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
