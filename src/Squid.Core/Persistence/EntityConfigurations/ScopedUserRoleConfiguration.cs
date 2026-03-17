using Squid.Core.Persistence.Entities.Account;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ScopedUserRoleConfiguration : IEntityTypeConfiguration<ScopedUserRole>
{
    public void Configure(EntityTypeBuilder<ScopedUserRole> builder)
    {
        builder.ToTable("scoped_user_role");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.TeamId).IsRequired();
        builder.Property(x => x.UserRoleId).IsRequired();
        builder.Property(x => x.SpaceId);
        builder.Property(x => x.CreatedDate).IsRequired();
        builder.Property(x => x.LastModifiedDate).IsRequired();

        builder.HasIndex(x => x.TeamId);
        builder.HasIndex(x => x.SpaceId);
    }
}
