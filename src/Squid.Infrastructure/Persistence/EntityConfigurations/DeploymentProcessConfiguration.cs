using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Squid.Infrastructure.Persistence.EntityConfigurations;

public class DeploymentProcessConfiguration: IEntityTypeConfiguration<DeploymentProcess>
{
    public void Configure(EntityTypeBuilder<DeploymentProcess> builder)
    {
        builder.ToTable("deployment_process");

        builder.HasKey(p => p.Id);
    }
}