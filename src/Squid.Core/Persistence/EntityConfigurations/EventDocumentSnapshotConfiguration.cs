using Squid.Core.Persistence.Entities.Events;

namespace Squid.Core.Persistence.EntityConfigurations;

public class EventDocumentSnapshotConfiguration : IEntityTypeConfiguration<EventDocumentSnapshot>
{
    public void Configure(EntityTypeBuilder<EventDocumentSnapshot> builder)
    {
        builder.ToTable("event_document_snapshot");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        builder.Property(p => p.BeforeJson).HasColumnType("jsonb");
        builder.Property(p => p.AfterJson).HasColumnType("jsonb");
    }
}
