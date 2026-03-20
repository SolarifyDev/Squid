using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ChannelVersionRuleConfiguration : IEntityTypeConfiguration<ChannelVersionRule>
{
    public void Configure(EntityTypeBuilder<ChannelVersionRule> builder)
    {
        builder.ToTable("channel_version_rules");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedOnAdd();

        builder.Property(r => r.ChannelId).IsRequired();
        builder.Property(r => r.ActionNames).HasDefaultValue("");
        builder.Property(r => r.VersionRange).HasDefaultValue("");
        builder.Property(r => r.PreReleaseTag).HasDefaultValue("");
        builder.Property(r => r.SortOrder).HasDefaultValue(0);

        builder.HasIndex(r => r.ChannelId);
    }
}
