using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Squid.Infrastructure.Persistence.EntityConfigurations;

public class LifecycleConfiguration: IEntityTypeConfiguration<Lifecycle>
{
    public void Configure(EntityTypeBuilder<Lifecycle> builder)
    {
        builder.ToTable("lifecycle");

        builder.HasKey(p => p.Id);
    }
}