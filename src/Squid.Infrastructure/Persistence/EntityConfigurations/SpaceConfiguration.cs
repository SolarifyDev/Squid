using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Squid.Infrastructure.Persistence.EntityConfigurations;

public class SpaceConfiguration: IEntityTypeConfiguration<Space>
{
    public void Configure(EntityTypeBuilder<Space> builder)
    {
        builder.ToTable("space");

        builder.HasKey(p => p.Id);
    }
}