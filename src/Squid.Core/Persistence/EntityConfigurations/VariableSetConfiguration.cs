namespace Squid.Core.Persistence.EntityConfigurations;

public class VariableSetConfiguration : IEntityTypeConfiguration<VariableSet>
{
    public void Configure(EntityTypeBuilder<VariableSet> builder)
    {
        builder.ToTable("variable_set");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        builder.Property(p => p.OwnerType)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(p => p.OwnerId)
            .IsRequired();

        builder.Property(p => p.SpaceId)
            .IsRequired();

        builder.Property(p => p.ContentHash)
            .HasMaxLength(64);

        builder.Property(p => p.Version)
            .HasDefaultValue(1);

        builder.Property(p => p.RelatedDocumentIds)
            .HasColumnType("TEXT");

        builder.HasIndex(vs => new { vs.OwnerType, vs.OwnerId })
            .HasDatabaseName("ix_variable_set_owner");

        builder.HasIndex(vs => vs.SpaceId)
            .HasDatabaseName("ix_variable_set_space");

        builder.HasIndex(vs => vs.ContentHash)
            .HasDatabaseName("ix_variable_set_content_hash");
    }
}
