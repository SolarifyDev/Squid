using Squid.Core.Persistence.Entities.Account;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ScopedUserRoleProjectConfiguration : IEntityTypeConfiguration<ScopedUserRoleProject>
{
    public void Configure(EntityTypeBuilder<ScopedUserRoleProject> builder)
    {
        builder.ToTable("scoped_user_role_project");

        builder.HasKey(x => new { x.ScopedUserRoleId, x.ProjectId });

        builder.Property(x => x.ScopedUserRoleId).IsRequired();
        builder.Property(x => x.ProjectId).IsRequired();
    }
}
