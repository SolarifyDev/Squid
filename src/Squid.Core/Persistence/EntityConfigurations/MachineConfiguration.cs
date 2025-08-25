namespace Squid.Core.Persistence.EntityConfigurations;

public class MachineConfiguration: IEntityTypeConfiguration<Machine>
{
    public void Configure(EntityTypeBuilder<Machine> builder)
    {
        builder.ToTable("machine");

        builder.HasKey(p => p.Id);
    }
}