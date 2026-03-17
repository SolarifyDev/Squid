using Squid.Core.Persistence.Entities.Account;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ScopedUserRoleEnvironmentConfiguration : IEntityTypeConfiguration<ScopedUserRoleEnvironment>
{
    public void Configure(EntityTypeBuilder<ScopedUserRoleEnvironment> builder)
    {
        builder.ToTable("scoped_user_role_environment");

        builder.HasKey(x => new { x.ScopedUserRoleId, x.EnvironmentId });

        builder.Property(x => x.ScopedUserRoleId).IsRequired();
        builder.Property(x => x.EnvironmentId).IsRequired();
    }
}
