using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Squid.Infrastructure.Persistence.EntityConfigurations;

public class DeploymentCompletionConfiguration: IEntityTypeConfiguration<DeploymentCompletion>
{
    public void Configure(EntityTypeBuilder<DeploymentCompletion> builder)
    {
        builder.ToTable("deployment_completion");

        builder.HasKey(p => p.Id);
    }
}