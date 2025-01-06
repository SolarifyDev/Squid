using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Squid.Infrastructure.Persistence.Postgres.EntityConfigurations;

public class DeploymentConfiguration: IEntityTypeConfiguration<Deployment>
{
    public void Configure(EntityTypeBuilder<Deployment> builder)
    {
        builder.ToTable("deployments");

        builder.HasKey(p => p.Id);
    }
}