using Squid.Core.Persistence.Data.Domain.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ActionEnvironmentConfiguration : IEntityTypeConfiguration<ActionEnvironment>
{
    public void Configure(EntityTypeBuilder<ActionEnvironment> builder)
    {
        builder.ToTable("action_environments");

        builder.HasKey(ae => new { ae.ActionId, ae.EnvironmentId });

        builder.Property(ae => ae.ActionId)
            .IsRequired();

        builder.Property(ae => ae.EnvironmentId)
            .IsRequired();
    }
}
