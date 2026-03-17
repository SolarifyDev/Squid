using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class DeploymentInterruptionConfiguration : IEntityTypeConfiguration<DeploymentInterruption>
{
    public void Configure(EntityTypeBuilder<DeploymentInterruption> builder)
    {
        builder.ToTable("deployment_interruption");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        builder.Property(p => p.InterruptionType).IsRequired();
        builder.Property(p => p.FormJson);
        builder.Property(p => p.SubmittedValuesJson);
        builder.Property(p => p.ResponsibleUserId);
        builder.Property(p => p.ResponsibleTeamIds);

        builder.HasIndex(p => p.ServerTaskId);
    }
}
