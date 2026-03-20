using Squid.Core.Persistence.Entities.Account;

namespace Squid.Core.Persistence.EntityConfigurations;

public class UserRolePermissionConfiguration : IEntityTypeConfiguration<UserRolePermission>
{
    public void Configure(EntityTypeBuilder<UserRolePermission> builder)
    {
        builder.ToTable("user_role_permission");

        builder.HasKey(x => new { x.UserRoleId, x.Permission });

        builder.Property(x => x.UserRoleId).IsRequired();
        builder.Property(x => x.Permission).HasMaxLength(100).IsRequired();
    }
}
