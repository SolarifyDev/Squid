namespace Squid.Infrastructure.Persistence;

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
            .As<ISquidDbContext>()
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
            .As<ISquidDbContext>()
            .InstancePerLifetimeScope();

        builder
            .Register(_ =>
                new PostgresDbUp(_squidStoreSetting.Postgres!.ConnectionString,
                    new DbUpLogger<PostgresDbUp>(_logger)))
            .As<IStartable>()
            .SingleInstance();
    }
}