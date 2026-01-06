using Squid.Core.Persistence.Data.Domain.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ChannelConfiguration: IEntityTypeConfiguration<Channel>
{
    public void Configure(EntityTypeBuilder<Channel> builder)
    {
        builder.ToTable("channel");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();
    }
}
