using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Squid.Core.Persistence.EntityConfigurations;

public class EnvironmentConfiguration: IEntityTypeConfiguration<Message.Domain.Deployments.Environment>
{
    public void Configure(EntityTypeBuilder<Message.Domain.Deployments.Environment> builder)
    {
        builder.ToTable("environment");

        builder.HasKey(p => p.Id);
    }
}
