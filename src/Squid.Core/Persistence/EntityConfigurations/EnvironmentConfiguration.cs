using Environment = Squid.Message.Domain.Deployments.Environment;

namespace Squid.Core.Persistence.EntityConfigurations;

public class EnvironmentConfiguration: IEntityTypeConfiguration<Environment>
{
    public void Configure(EntityTypeBuilder<Environment> builder)
    {
        builder.ToTable("environment");

        builder.HasKey(p => p.Id);
    }
}
