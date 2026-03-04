using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class LifecyclePhaseConfiguration : IEntityTypeConfiguration<LifecyclePhase>
{
    public void Configure(EntityTypeBuilder<LifecyclePhase> builder)
    {
        builder.ToTable("phase");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();
    }
}
