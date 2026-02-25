using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class LifecyclePhaseEnvironmentConfiguration : IEntityTypeConfiguration<LifecyclePhaseEnvironment>
{
    public void Configure(EntityTypeBuilder<LifecyclePhaseEnvironment> builder)
    {
        builder.ToTable("phase_environment");

        builder.HasKey(pe => new { pe.PhaseId, pe.EnvironmentId });

        builder.Property(pe => pe.PhaseId)
            .IsRequired();

        builder.Property(pe => pe.EnvironmentId)
            .IsRequired();
    }
}
