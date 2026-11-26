using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class PackageVersionRuleConfiguration : IEntityTypeConfiguration<PackageVersionRule>
{
    public void Configure(EntityTypeBuilder<PackageVersionRule> builder)
    {
        builder.ToTable("package_version_rule");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedOnAdd();

        builder.HasIndex(r => new { r.FeedId, r.PackageId }).IsUnique();
    }
}
