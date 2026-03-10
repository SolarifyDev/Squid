using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class DeploymentInterruptionConfiguration : IEntityTypeConfiguration<DeploymentInterruption>
{
    public void Configure(EntityTypeBuilder<DeploymentInterruption> builder)
    {
        builder.ToTable("deployment_interruption");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        builder.HasIndex(p => p.ServerTaskId);
    }
}
