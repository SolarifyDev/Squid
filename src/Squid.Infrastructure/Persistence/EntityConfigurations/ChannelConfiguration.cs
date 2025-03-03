using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Squid.Infrastructure.Persistence.EntityConfigurations;

public class ChannelConfiguration: IEntityTypeConfiguration<Channel>
{
    public void Configure(EntityTypeBuilder<Channel> builder)
    {
        builder.ToTable("channel");

        builder.HasKey(p => p.Id);
    }
}