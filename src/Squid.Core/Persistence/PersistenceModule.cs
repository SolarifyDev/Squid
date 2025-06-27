namespace Squid.Core.Persistence;

public class PersistenceModule : Module
{
    private readonly SquidStoreSetting _squidStoreSetting;
    private readonly ILogger _logger;

    public PersistenceModule(SquidStoreSetting squidStoreSetting, ILogger logger)
    {
        _squidStoreSetting = squidStoreSetting;
        _logger = logger;
    }

    protected override void Load(ContainerBuilder builder)
    {
        switch (_squidStoreSetting.Type)
        {
            case SquidStoreSetting.SquidStoreType.Volatile:
                RegisterInMemoryDbContext(builder);
                break;
            case SquidStoreSetting.SquidStoreType.Postgres:
                RegisterPostgresDbContext(builder);
                break;
            case SquidStoreSetting.SquidStoreType.MySql:
                break;
        }
        
        builder.RegisterType<EfRepository>().As<IRepository>().InstancePerLifetimeScope();
    }

    private void RegisterInMemoryDbContext(ContainerBuilder builder)
    {
        builder.Register(_ =>
            {
                var dbContextBuilder = new DbContextOptionsBuilder<SquidDbContext>();

                dbContextBuilder.UseInMemoryDatabase("Squid");

                return new SquidDbContext(dbContextBuilder.Options);
            })
            .AsSelf()
            .As<DbContext>()
            .AsImplementedInterfaces()
            .InstancePerLifetimeScope();
    }

    private void RegisterPostgresDbContext(ContainerBuilder builder)
    {
        builder.Register(_ =>
            {
                var dbContextBuilder = new DbContextOptionsBuilder<SquidDbContext>();

                dbContextBuilder
                    .UseNpgsql(_squidStoreSetting.Postgres!.ConnectionString)
                    .UseSnakeCaseNamingConvention();

                return new SquidDbContext(dbContextBuilder.Options);
            })
            .AsSelf()
            .As<DbContext>()
            .AsImplementedInterfaces()
            .InstancePerLifetimeScope();

        builder
            .Register(_ =>
                new PostgresDbUp(_squidStoreSetting.Postgres!.ConnectionString,
                    new DbUpLogger<PostgresDbUp>(_logger)))
            .As<IStartable>()
            .SingleInstance();
    }
}