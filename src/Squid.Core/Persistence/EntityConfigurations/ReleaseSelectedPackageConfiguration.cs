using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ReleaseSelectedPackageConfiguration : IEntityTypeConfiguration<ReleaseSelectedPackage>
{
    public void Configure(EntityTypeBuilder<ReleaseSelectedPackage> builder)
    {
        builder.ToTable("release_selected_package");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();
    }
}
