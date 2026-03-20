using Squid.Core.Persistence.Entities.Account;

namespace Squid.Core.Persistence.EntityConfigurations;

public class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> builder)
    {
        builder.ToTable("team_member");

        builder.HasKey(tm => new { tm.TeamId, tm.UserId });

        builder.Property(tm => tm.TeamId).IsRequired();
        builder.Property(tm => tm.UserId).IsRequired();
    }
}
