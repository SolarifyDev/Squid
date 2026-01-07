using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class MachinePolicyConfiguration : IEntityTypeConfiguration<MachinePolicy>
{
    public void Configure(EntityTypeBuilder<MachinePolicy> builder)
    {
        builder.ToTable("machine_policy");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();
    }
}
