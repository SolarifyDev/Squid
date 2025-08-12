namespace Squid.Core.Persistence.EntityConfigurations;

public class ReleaseVariableSnapshotConfiguration : IEntityTypeConfiguration<ReleaseVariableSnapshot>
{
    public void Configure(EntityTypeBuilder<ReleaseVariableSnapshot> builder)
    {
        builder.ToTable("release_variable_snapshot");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        builder.Property(p => p.ReleaseId)
            .IsRequired();

        builder.Property(p => p.VariableSetId)
            .IsRequired();

        builder.Property(p => p.SnapshotId)
            .IsRequired();

        builder.Property(p => p.VariableSetType)
            .IsRequired()
            .HasConversion<int>()
            .HasDefaultValue(1); // ReleaseVariableSetType.Project = 1

        builder.HasIndex(rs => new { rs.ReleaseId, rs.VariableSetId })
            .IsUnique()
            .HasDatabaseName("uk_release_variable_set");

        builder.HasIndex(rs => rs.ReleaseId)
            .HasDatabaseName("ix_release_snapshot_release");

        builder.HasIndex(rs => rs.VariableSetId)
            .HasDatabaseName("ix_release_snapshot_variable_set");
    }
}
