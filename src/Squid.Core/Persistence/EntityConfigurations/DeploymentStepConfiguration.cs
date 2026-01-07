using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class DeploymentStepConfiguration : IEntityTypeConfiguration<DeploymentStep>
{
    public void Configure(EntityTypeBuilder<DeploymentStep> builder)
    {
        builder.ToTable("deployment_step");

        builder.HasKey(ds => ds.Id);
        builder.Property(ds => ds.Id).ValueGeneratedOnAdd();

        builder.Property(ds => ds.ProcessId)
            .IsRequired();

        builder.Property(ds => ds.StepOrder)
            .IsRequired();

        builder.Property(ds => ds.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(ds => ds.StepType)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(ds => ds.Condition)
            .HasColumnType("TEXT");

        builder.Property(ds => ds.StartTrigger)
            .HasMaxLength(50);

        builder.Property(ds => ds.PackageRequirement)
            .HasMaxLength(50);

        builder.Property(ds => ds.IsDisabled)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(ds => ds.IsRequired)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(ds => ds.CreatedAt)
            .IsRequired()
            .HasDefaultValueSql("NOW()");
    }
}
