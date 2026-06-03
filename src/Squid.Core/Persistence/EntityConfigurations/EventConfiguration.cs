using Squid.Core.Persistence.Entities.Events;

namespace Squid.Core.Persistence.EntityConfigurations;

public class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("event");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        // Map the short-backed enums to smallint (else EF maps enum -> int4 and
        // mismatches the int2 column) and the structured references to jsonb.
        builder.Property(p => p.Category).HasColumnType("smallint");
        builder.Property(p => p.EstablishedWith).HasColumnType("smallint");
        builder.Property(p => p.ReferencesJson).HasColumnType("jsonb");

        // Indexes are created by the DbUp migration (the DDL source of truth).
    }
}
