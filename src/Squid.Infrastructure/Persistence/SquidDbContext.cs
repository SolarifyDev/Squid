using Squid.Infrastructure.Persistence.EntityConfigurations;

namespace Squid.Infrastructure.Persistence;

public class SquidDbContext : DbContext, ISquidDbContext
{
    public SquidDbContext(DbContextOptions<SquidDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DeploymentConfiguration).Assembly);
    }

    public DbSet<Customer> Customers { get; set; }
    public DbSet<Deployment> Deployments { get; set; }
}