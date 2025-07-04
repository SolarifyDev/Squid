using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Squid.Core.Persistence.EntityConfigurations;

public class DeploymentEnvironmentConfiguration: IEntityTypeConfiguration<DeploymentEnvironment>
{
    public void Configure(EntityTypeBuilder<DeploymentEnvironment> builder)
    {
        builder.ToTable("deployment_environment");

        builder.HasKey(p => p.Id);
    }
}