using Squid.Message.Domain.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class DeploymentActionConfiguration : IEntityTypeConfiguration<DeploymentAction>
{
    public void Configure(EntityTypeBuilder<DeploymentAction> builder)
    {
        builder.ToTable("deployment_action");

        builder.HasKey(da => da.Id);
        builder.Property(da => da.Id).ValueGeneratedOnAdd();

        builder.Property(da => da.StepId)
            .IsRequired();

        builder.Property(da => da.ActionOrder)
            .IsRequired();

        builder.Property(da => da.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(da => da.ActionType)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(da => da.WorkerPoolId);

        builder.Property(da => da.IsDisabled)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(da => da.IsRequired)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(da => da.CanBeUsedForProjectVersioning)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(da => da.CreatedAt)
            .IsRequired()
            .HasDefaultValueSql("NOW()");
    }
}
