namespace Squid.Core.Persistence.EntityConfigurations;

public class VariableConfiguration : IEntityTypeConfiguration<Variable>
{
    public void Configure(EntityTypeBuilder<Variable> builder)
    {
        builder.ToTable("variable");

        builder.HasKey(p => p.Id);
    }
}
