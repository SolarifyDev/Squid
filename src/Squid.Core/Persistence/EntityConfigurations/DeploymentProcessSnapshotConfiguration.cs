using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class DeploymentProcessSnapshotConfiguration : IEntityTypeConfiguration<DeploymentProcessSnapshot>
{
    public void Configure(EntityTypeBuilder<DeploymentProcessSnapshot> builder)
    {
        builder.ToTable("deployment_process_snapshot");

        builder.HasKey(p => p.Id);
    }
}
