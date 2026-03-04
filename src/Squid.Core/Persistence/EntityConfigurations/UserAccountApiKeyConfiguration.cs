using Squid.Core.Persistence.Entities.Account;

namespace Squid.Core.Persistence.EntityConfigurations;

public class UserAccountApiKeyConfiguration : IEntityTypeConfiguration<UserAccountApiKey>
{
    public void Configure(EntityTypeBuilder<UserAccountApiKey> builder)
    {
        builder.ToTable("user_account_api_key");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.UserAccountId).IsRequired();
        builder.Property(x => x.ApiKey).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(256);
        builder.Property(x => x.CreatedAtUtc).IsRequired();

        builder.HasIndex(x => x.ApiKey).IsUnique();
    }
}
