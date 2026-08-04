using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class OctopusImportSessionConfiguration : IEntityTypeConfiguration<OctopusImportSession>
{
    public void Configure(EntityTypeBuilder<OctopusImportSession> builder)
    {
        builder.ToTable("octopus_import_session");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedOnAdd();

        builder.Property(s => s.SessionId).IsRequired();
        builder.Property(s => s.DestinationSpaceId).IsRequired();
        builder.Property(s => s.OwnerUserId).IsRequired();
        builder.Property(s => s.State).HasMaxLength(32).IsRequired();
        builder.Property(s => s.SourceSummaryJson).HasColumnType("jsonb").HasDefaultValue("{}");
        builder.Property(s => s.RedactedNormalizedDataJson).HasColumnType("jsonb");
        builder.Property(s => s.ValidatedPlanJson).HasColumnType("jsonb");
        builder.Property(s => s.ResultJson).HasColumnType("jsonb");
        builder.Property(s => s.DataVersion).IsConcurrencyToken();
        builder.Property(s => s.ExpiresAt).IsRequired();
        builder.Property(s => s.LastStateChangedAt).IsRequired();

        builder.HasIndex(s => s.SessionId).IsUnique();
        builder.HasIndex(s => new { s.OwnerUserId, s.DestinationSpaceId, s.SessionId });
        builder.HasIndex(s => new { s.DestinationSpaceId, s.State });
        builder.HasIndex(s => s.ExpiresAt);
    }
}
