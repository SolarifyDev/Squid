using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class RunbookRunConfiguration : IEntityTypeConfiguration<RunbookRun>
{
    public void Configure(EntityTypeBuilder<RunbookRun> builder)
    {
        builder.ToTable("runbook_run");

        builder.HasKey(run => run.Id);
        builder.Property(run => run.Id).ValueGeneratedOnAdd();
        builder.HasIndex(run => run.ServerTaskId).IsUnique();
        builder.HasIndex(run => new { run.RunbookId, run.CreatedDate })
            .HasDatabaseName("ix_runbook_run_runbook_created");

        builder.HasOne<Runbook>()
            .WithMany()
            .HasForeignKey(run => run.RunbookId)
            .HasConstraintName("fk_runbook_run_runbook")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<RunbookSnapshot>()
            .WithMany()
            .HasForeignKey(run => new { run.RunbookId, run.SnapshotId })
            .HasPrincipalKey(snapshot => new { snapshot.RunbookId, snapshot.Id })
            .HasConstraintName("fk_runbook_run_snapshot")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(run => run.Task)
            .WithOne()
            .HasForeignKey<RunbookRun>(run => run.ServerTaskId)
            .HasConstraintName("fk_runbook_run_server_task")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
