using Squid.Message.Domain.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ActionChannelConfiguration : IEntityTypeConfiguration<ActionChannel>
{
    public void Configure(EntityTypeBuilder<ActionChannel> builder)
    {
        builder.ToTable("action_channels");

        builder.HasKey(ac => new { ac.ActionId, ac.ChannelId });

        builder.Property(ac => ac.ActionId)
            .IsRequired();

        builder.Property(ac => ac.ChannelId)
            .IsRequired();
    }
}
