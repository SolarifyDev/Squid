using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class OctopusImportSourceMapConfiguration : IEntityTypeConfiguration<OctopusImportSourceMap>
{
    public void Configure(EntityTypeBuilder<OctopusImportSourceMap> builder)
    {
        builder.ToTable("octopus_import_source_map");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedOnAdd();

        builder.Property(m => m.SpaceId).IsRequired();
        builder.Property(m => m.SourceType).HasMaxLength(64).IsRequired();
        builder.Property(m => m.SourceId).HasMaxLength(200).IsRequired();
        builder.Property(m => m.DestinationId).IsRequired();
        builder.Property(m => m.OutcomeState).HasMaxLength(16).IsRequired();
        builder.Property(m => m.Fingerprint).HasMaxLength(64);
        builder.Property(m => m.DetailFingerprint).HasMaxLength(64);

        builder.HasIndex(m => new { m.SpaceId, m.SourceType, m.SourceId }).IsUnique();
        builder.HasIndex(m => new { m.SourceType, m.DestinationId });
    }
}
