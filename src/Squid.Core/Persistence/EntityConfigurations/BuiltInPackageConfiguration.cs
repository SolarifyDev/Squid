using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class BuiltInPackageConfiguration : IEntityTypeConfiguration<BuiltInPackage>
{
    public void Configure(EntityTypeBuilder<BuiltInPackage> builder)
    {
        builder.ToTable("builtin_package");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();
        builder.Property(p => p.Facts).HasColumnType("jsonb");
    }
}
