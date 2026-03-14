using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities;
using Squid.Core.Persistence.EntityConfigurations;
using Squid.Core.Services.Identity;
using Squid.Message.Constants;

namespace Squid.Core.Persistence;

public class SquidDbContext : DbContext, IUnitOfWork
{
    private readonly ICurrentUser _currentUser;

    public SquidDbContext(DbContextOptions<SquidDbContext> options, ICurrentUser currentUser = null) : base(options)
    {
        _currentUser = currentUser;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DeploymentConfiguration).Assembly);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ChangeTracker.DetectChanges();
        ApplyAuditFields();

        return await base.SaveChangesAsync(cancellationToken);
    }

    private void ApplyAuditFields()
    {
        var now = DateTimeOffset.UtcNow;
        var userId = _currentUser?.Id ?? CurrentUsers.InternalUser.Id;

        foreach (var entry in ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedDate = now;
                entry.Entity.CreatedBy = userId;
                entry.Entity.LastModifiedDate = now;
                entry.Entity.LastModifiedBy = userId;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.LastModifiedDate = now;
                entry.Entity.LastModifiedBy = userId;
            }
        }
    }

    public bool ShouldSaveChanges { get; set; }
}