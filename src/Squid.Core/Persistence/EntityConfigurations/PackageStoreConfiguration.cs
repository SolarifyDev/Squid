using Squid.Core.Persistence.Entities.PackageStorage;

namespace Squid.Core.Persistence.EntityConfigurations;

public class PackageStoreConfiguration : IEntityTypeConfiguration<PackageStore>
{
    public void Configure(EntityTypeBuilder<PackageStore> builder)
    {
        builder.ToTable("package_store");

        builder.HasKey(p => p.Id);
    }
}
