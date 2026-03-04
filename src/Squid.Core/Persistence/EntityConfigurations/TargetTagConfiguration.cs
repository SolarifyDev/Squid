using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class TargetTagConfiguration : IEntityTypeConfiguration<TargetTag>
{
    public void Configure(EntityTypeBuilder<TargetTag> builder)
    {
        builder.ToTable("target_tag");

        builder.HasKey(p => p.Id);
    }
}
