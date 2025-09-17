using Squid.Message.Domain.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class DeploymentActionPropertyConfiguration : IEntityTypeConfiguration<DeploymentActionProperty>
{
    public void Configure(EntityTypeBuilder<DeploymentActionProperty> builder)
    {
        builder.ToTable("deployment_action_property");

        builder.HasKey(dap => new { dap.ActionId, dap.PropertyName });

        builder.Property(dap => dap.ActionId)
            .IsRequired();

        builder.Property(dap => dap.PropertyName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(dap => dap.PropertyValue)
            .HasColumnType("TEXT");
    }
}
