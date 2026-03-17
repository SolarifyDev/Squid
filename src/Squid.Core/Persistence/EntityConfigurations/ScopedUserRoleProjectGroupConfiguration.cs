using Squid.Core.Persistence.Entities.Account;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ScopedUserRoleProjectGroupConfiguration : IEntityTypeConfiguration<ScopedUserRoleProjectGroup>
{
    public void Configure(EntityTypeBuilder<ScopedUserRoleProjectGroup> builder)
    {
        builder.ToTable("scoped_user_role_project_group");

        builder.HasKey(x => new { x.ScopedUserRoleId, x.ProjectGroupId });

        builder.Property(x => x.ScopedUserRoleId).IsRequired();
        builder.Property(x => x.ProjectGroupId).IsRequired();
    }
}
