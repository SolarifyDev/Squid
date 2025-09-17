using Squid.Message.Domain.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ActionTenantTagConfiguration : IEntityTypeConfiguration<ActionTenantTag>
{
    public void Configure(EntityTypeBuilder<ActionTenantTag> builder)
    {
        builder.ToTable("action_tenant_tags");

        builder.HasKey(att => new { att.ActionId, att.TenantTag });

        builder.Property(att => att.ActionId)
            .IsRequired();

        builder.Property(att => att.TenantTag)
            .IsRequired()
            .HasMaxLength(100);
    }
}
