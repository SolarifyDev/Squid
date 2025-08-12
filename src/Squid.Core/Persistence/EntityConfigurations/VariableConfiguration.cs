namespace Squid.Core.Persistence.EntityConfigurations;

public class VariableConfiguration : IEntityTypeConfiguration<Variable>
{
    public void Configure(EntityTypeBuilder<Variable> builder)
    {
        builder.ToTable("variable");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        builder.Property(p => p.VariableSetId)
            .IsRequired();

        builder.Property(p => p.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(p => p.Value)
            .HasColumnType("TEXT");

        builder.Property(p => p.Description)
            .HasColumnType("TEXT");

        builder.Property(p => p.Type)
            .IsRequired()
            .HasConversion<int>()
            .HasDefaultValue(1); // VariableType.String = 1

        builder.Property(p => p.IsSensitive)
            .HasDefaultValue(false);

        builder.Property(p => p.SortOrder)
            .HasDefaultValue(0);

        builder.Property(p => p.LastModifiedBy)
            .HasMaxLength(255);

        builder.HasIndex(v => new { v.VariableSetId, v.Name })
            .HasDatabaseName("ix_variable_variable_set_name");

        builder.HasIndex(v => new { v.VariableSetId, v.SortOrder, v.Name })
            .HasDatabaseName("ix_variable_sort_order");
    }
}
