using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ActionExcludedEnvironmentConfiguration : IEntityTypeConfiguration<ActionExcludedEnvironment>
{
    public void Configure(EntityTypeBuilder<ActionExcludedEnvironment> builder)
    {
        builder.ToTable("action_excluded_environments");

        builder.HasKey(ae => new { ae.ActionId, ae.EnvironmentId });

        builder.Property(ae => ae.ActionId)
            .IsRequired();

        builder.Property(ae => ae.EnvironmentId)
            .IsRequired();
    }
}
