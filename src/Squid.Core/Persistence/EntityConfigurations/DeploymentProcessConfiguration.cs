using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Squid.Core.Persistence.EntityConfigurations;

public class DeploymentProcessConfiguration: IEntityTypeConfiguration<DeploymentProcess>
{
    public void Configure(EntityTypeBuilder<DeploymentProcess> builder)
    {
        builder.ToTable("deployment_process");

        builder.HasKey(p => p.Id);
    }
}