using DbUp;
using DbUp.Builder;
using Squid.Core.Persistence.Db;

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
                RegisterMySqlDbContext(builder);
                break;
        }
        
        RegisterDbUpRunner(_squidStoreSetting.Type, builder);
        
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
    }
    
    private void RegisterMySqlDbContext(ContainerBuilder builder)
    {
        builder.Register(_ =>
            {
                var dbContextBuilder = new DbContextOptionsBuilder<SquidDbContext>();

                dbContextBuilder.UseMySql(_squidStoreSetting.MySql!.ConnectionString, new MySqlServerVersion(new Version(8, 0, 28)), optionsBuilder =>
                {
                    optionsBuilder.CommandTimeout(6000);
                });

                return new SquidDbContext(dbContextBuilder.Options);
            })
            .AsSelf()
            .As<DbContext>()
            .AsImplementedInterfaces()
            .InstancePerLifetimeScope();
    }

    private void RegisterDbUpRunner(SquidStoreSetting.SquidStoreType type, ContainerBuilder builder)
    {
        if (type == SquidStoreSetting.SquidStoreType.Volatile) return;

        string connectionString;
        string scriptFolderName;
        UpgradeEngineBuilder engineBuilder;
        
        switch (_squidStoreSetting.Type)
        {
            case SquidStoreSetting.SquidStoreType.Postgres:
                (connectionString, scriptFolderName) = (_squidStoreSetting.Postgres!.ConnectionString, "Persistence/Db/Postgres/Scripts");
                EnsureDatabase.For.PostgresqlDatabase(connectionString);
                engineBuilder = DeployChanges.To.PostgresqlDatabase(connectionString);
                break;
            case SquidStoreSetting.SquidStoreType.MySql:
                (connectionString, scriptFolderName) = (_squidStoreSetting.MySql!.ConnectionString, "Persistence/Db/MySql/Scripts");
                EnsureDatabase.For.MySqlDatabase(connectionString);
                engineBuilder = DeployChanges.To.MySqlDatabase(connectionString);
                break;
            default:
                throw new NotSupportedException(nameof(_squidStoreSetting.Type));
        }
        
        builder
            .Register(_ => new DbUpRunner(engineBuilder, new DbUpLogger<DbUpRunner>(_logger), scriptFolderName))
            .As<IStartable>()
            .SingleInstance();
    }
}