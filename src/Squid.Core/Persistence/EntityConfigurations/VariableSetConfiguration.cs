namespace Squid.Core.Persistence.EntityConfigurations;

public class VariableSetConfiguration : IEntityTypeConfiguration<VariableSet>
{
    public void Configure(EntityTypeBuilder<VariableSet> builder)
    {
        builder.ToTable("variable_set");

        builder.HasKey(p => p.Id);
    }
}
