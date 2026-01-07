using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ExternalFeedConfiguration: IEntityTypeConfiguration<ExternalFeed>
{
    public void Configure(EntityTypeBuilder<ExternalFeed> builder)
    {
        builder.ToTable("external_feed");

        builder.HasKey(p => p.Id);
    }
}