using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Squid.Infrastructure.Persistence.EntityConfigurations;

public class ReleaseConfiguration: IEntityTypeConfiguration<Release>
{
    public void Configure(EntityTypeBuilder<Release> builder)
    {
        builder.ToTable("release");

        builder.HasKey(p => p.Id);
    }
}