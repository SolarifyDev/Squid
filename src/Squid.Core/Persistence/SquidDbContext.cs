using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities;
using Squid.Core.Persistence.EntityConfigurations;
using Squid.Core.Services.Events;
using Squid.Core.Services.Identity;
using Squid.Message.Constants;

namespace Squid.Core.Persistence;

public partial class SquidDbContext : DbContext, IUnitOfWork
{
    private readonly ICurrentUser _currentUser;
    private readonly IAuditDocumentRegistry _auditDocuments;

    public SquidDbContext(DbContextOptions<SquidDbContext> options, ICurrentUser currentUser = null, IAuditDocumentRegistry auditDocuments = null) : base(options)
    {
        _currentUser = currentUser;
        _auditDocuments = auditDocuments;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DeploymentConfiguration).Assembly);

        modelBuilder.HasDbFunction(typeof(PostgresFunctions).GetMethod(nameof(PostgresFunctions.JsonValue))!).HasName("jsonb_extract_path_text");
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ChangeTracker.DetectChanges();
        ApplyAuditFields();

        var documentAudits = CaptureDocumentAudits();

        var result = await base.SaveChangesAsync(cancellationToken);

        await EmitDocumentAuditsAsync(documentAudits, cancellationToken);

        return result;
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
