namespace Squid.Core.Persistence.EntityConfigurations;

public class VariableScopeConfiguration : IEntityTypeConfiguration<VariableScope>
{
    public void Configure(EntityTypeBuilder<VariableScope> builder)
    {
        builder.ToTable("variable_scope");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        builder.Property(p => p.ScopeType)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(p => p.ScopeValue)
            .IsRequired()
            .HasMaxLength(100);

        builder.HasIndex(s => s.VariableId)
            .HasDatabaseName("ix_variable_scope_variable");

        builder.HasIndex(s => new { s.ScopeType, s.ScopeValue })
            .HasDatabaseName("ix_variable_scope_type_value");
    }
}
