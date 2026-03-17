using Squid.Core.Persistence.Entities.Account;

namespace Squid.Core.Persistence.EntityConfigurations;

public class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("user_role");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description);
        builder.Property(x => x.IsBuiltIn).IsRequired();
        builder.Property(x => x.CreatedDate).IsRequired();
        builder.Property(x => x.LastModifiedDate).IsRequired();

        builder.HasIndex(x => x.Name).IsUnique();
    }
}
