using Squid.Infrastructure.Persistence.Postgres.EntityConfigurations;

namespace Squid.Infrastructure.Persistence;

public class SquidDbContext : DbContext, ISquidDbContext
{
    private readonly SquidStoreSetting _storeSetting;

    public SquidDbContext(SquidStoreSetting storeSetting)
    {
        _storeSetting = storeSetting;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        switch (_storeSetting.Type)
        {
            case SquidStoreSetting.SquidStoreType.Volatile:
                optionsBuilder.UseInMemoryDatabase("Squid");
                break;
            case SquidStoreSetting.SquidStoreType.Postgres:
                optionsBuilder.UseNpgsql(_storeSetting.Postgres!.ConnectionString);
                break;
            case SquidStoreSetting.SquidStoreType.MySql:
            default:
                break;
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        switch (_storeSetting.Type)
        {
            case SquidStoreSetting.SquidStoreType.Volatile:
            case SquidStoreSetting.SquidStoreType.Postgres:
                modelBuilder.ApplyConfigurationsFromAssembly(typeof(DeploymentConfiguration).Assembly);
                break;
            case SquidStoreSetting.SquidStoreType.MySql:
            default:
                break;
        }
    }

    public DbSet<Customer> Customers { get; set; }
    public DbSet<Deployment> Deployments { get; set; }
}