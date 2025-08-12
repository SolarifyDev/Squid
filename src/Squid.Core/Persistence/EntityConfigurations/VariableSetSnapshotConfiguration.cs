namespace Squid.Core.Persistence.EntityConfigurations;

public class VariableSetSnapshotConfiguration : IEntityTypeConfiguration<VariableSetSnapshot>
{
    public void Configure(EntityTypeBuilder<VariableSetSnapshot> builder)
    {
        builder.ToTable("variable_set_snapshot");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        builder.Property(p => p.OriginalVariableSetId)
            .IsRequired();

        builder.Property(p => p.ContentHash)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(p => p.CompressionType)
            .IsRequired()
            .HasMaxLength(20)
            .HasDefaultValue("GZIP");

        builder.Property(p => p.CreatedBy)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(p => p.SnapshotData)
            .IsRequired()
            .HasColumnType("BYTEA");

        builder.HasIndex(s => new { s.OriginalVariableSetId, s.ContentHash })
            .HasDatabaseName("ix_snapshot_original_hash");

        builder.HasIndex(s => s.CreatedAt)
            .HasDatabaseName("ix_snapshot_created")
            .IsDescending();

        builder.HasIndex(s => s.ContentHash)
            .HasDatabaseName("ix_snapshot_content_hash");
    }
}
