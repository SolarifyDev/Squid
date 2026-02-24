using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ActivityLogConfiguration : IEntityTypeConfiguration<ActivityLog>
{
    public void Configure(EntityTypeBuilder<ActivityLog> builder)
    {
        builder.ToTable("activity_log");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        builder.Property(p => p.ServerTaskId).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(500);
        builder.Property(p => p.NodeType).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(p => p.Category).HasConversion<string>().HasMaxLength(50);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(50);
        builder.Property(p => p.SortOrder).IsRequired();

        builder.HasIndex(p => p.ServerTaskId);
        builder.HasIndex(p => p.ParentId);
    }
}
