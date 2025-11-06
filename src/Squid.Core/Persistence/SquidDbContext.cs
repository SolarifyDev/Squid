using Squid.Core.Persistence.EntityConfigurations;

namespace Squid.Core.Persistence;

public class SquidDbContext : DbContext, IUnitOfWork
{
    public SquidDbContext(DbContextOptions<SquidDbContext> options) : base(options)
    {
    }

    public DbSet<Squid.Core.Infrastructure.Domain.Deployments.ServerTask> ServerTasks { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DeploymentConfiguration).Assembly);
    }
    
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ChangeTracker.DetectChanges();
        
        return await base.SaveChangesAsync(cancellationToken);
    }
    
    public bool ShouldSaveChanges { get; set; }
}
