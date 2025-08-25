namespace Squid.Core.Persistence.EntityConfigurations;

public class VariableScopeConfiguration : IEntityTypeConfiguration<VariableScope>
{
    public void Configure(EntityTypeBuilder<VariableScope> builder)
    {
        builder.ToTable("variable_scope");

        builder.HasKey(p => p.Id);
    }
}
