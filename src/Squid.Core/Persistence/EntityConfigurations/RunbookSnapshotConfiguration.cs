using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class RunbookSnapshotConfiguration : IEntityTypeConfiguration<RunbookSnapshot>
{
    public void Configure(EntityTypeBuilder<RunbookSnapshot> builder)
    {
        builder.ToTable("runbook_snapshot");
        builder.HasKey(s => s.Id);
        builder.HasAlternateKey(s => new { s.RunbookId, s.Id });
        builder.Property(s => s.Version).IsRequired();
        builder.Property(s => s.SnapshotData).IsRequired();
        builder.Property(s => s.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(s => s.CompressionType).HasMaxLength(16).IsRequired();
        builder.Property(s => s.UncompressedSize).IsRequired();

        builder.HasIndex(s => new { s.RunbookId, s.Version })
            .IsUnique()
            .HasDatabaseName("uq_runbook_snapshot_version");
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_runbook_snapshot_version", "version > 0"));

        builder.HasOne<Runbook>().WithMany()
            .HasForeignKey(s => s.RunbookId)
            .HasConstraintName("fk_runbook_snapshot_runbook")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
