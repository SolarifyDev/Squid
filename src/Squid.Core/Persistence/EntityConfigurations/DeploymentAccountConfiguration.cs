using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class DeploymentAccountConfiguration : IEntityTypeConfiguration<DeploymentAccount>
{
    public void Configure(EntityTypeBuilder<DeploymentAccount> builder)
    {
        builder.ToTable("deployment_account");
        
        builder.HasKey(da => da.Id);
        builder.Property(da => da.Id).ValueGeneratedOnAdd();
    }
}