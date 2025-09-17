using Squid.Message.Domain.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class DeploymentStepPropertyConfiguration : IEntityTypeConfiguration<DeploymentStepProperty>
{
    public void Configure(EntityTypeBuilder<DeploymentStepProperty> builder)
    {
        builder.ToTable("deployment_step_property");

        builder.HasKey(dsp => new { dsp.StepId, dsp.PropertyName });

        builder.Property(dsp => dsp.StepId)
            .IsRequired();

        builder.Property(dsp => dsp.PropertyName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(dsp => dsp.PropertyValue)
            .HasColumnType("TEXT");
    }
}
