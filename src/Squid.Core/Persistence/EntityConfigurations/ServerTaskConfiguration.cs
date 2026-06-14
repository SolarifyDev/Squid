using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ServerTaskConfiguration: IEntityTypeConfiguration<ServerTask>
{
    /// <summary>
    /// Name of the UNIQUE partial index that enforces "at most one ACTIVE task per
    /// ConcurrencyTag" at the database level — the cross-process (multi-pod) atomic slot
    /// guarantee. "Active" = Executing, Paused, or Cancelling: a paused/cancelling deployment
    /// still holds the slot because its in-flight agent script may be running (preserved for
    /// resume). Created by <c>20260614_add_unique_active_deployment_per_concurrency_tag.sql</c>
    /// and matched by name in the data provider to map the 23505 violation to
    /// <see cref="Squid.Core.Services.Deployments.ServerTask.Exceptions.ConcurrencySlotOccupiedException"/>.
    /// Renaming it requires updating the migration, this constant, and the pin test together.
    /// </summary>
    public const string OneActivePerTagIndexName = "ux_server_task_active_per_tag";

    public void Configure(EntityTypeBuilder<ServerTask> builder)
    {
        builder.ToTable("server_task");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        builder.Property(p => p.DataVersion).IsConcurrencyToken();
        builder.Property(p => p.State).HasMaxLength(50).IsRequired();
        builder.Property(p => p.HasPendingInterruptions);

        builder.HasIndex(p => new { p.ConcurrencyTag, p.State })
            .HasFilter("concurrency_tag IS NOT NULL")
            .HasDatabaseName("ix_server_task_concurrency_tag_state");

        // Cross-process atomic slot: at most ONE ACTIVE task (Executing/Paused/Cancelling) per
        // ConcurrencyTag. Paused/Cancelling count as occupying because their in-flight agent
        // script may still be running. The only transition that adds a second row to the active
        // set is Pending/Paused→Executing, which then violates this index and is mapped to
        // ConcurrencySlotOccupiedException — so two deployments to the same environment cannot
        // run concurrently regardless of how many pods race the (non-atomic) free-slot poll.
        // NOTE: schema is created by the DbUp migration, not by EF; keep this filter byte-identical
        // to that migration's WHERE clause.
        builder.HasIndex(p => p.ConcurrencyTag)
            .IsUnique()
            .HasFilter("concurrency_tag IS NOT NULL AND state IN ('Executing', 'Paused', 'Cancelling')")
            .HasDatabaseName(OneActivePerTagIndexName);
    }
}
