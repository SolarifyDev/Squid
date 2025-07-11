using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Squid.Core.Persistence.EntityConfigurations;

public class DeploymentConfiguration: IEntityTypeConfiguration<Deployment>
{
    public void Configure(EntityTypeBuilder<Deployment> builder)
    {
        builder.ToTable("deployment");

        builder.HasKey(p => p.Id);
    }
}