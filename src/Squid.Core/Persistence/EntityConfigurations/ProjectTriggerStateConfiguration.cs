using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public sealed class ProjectTriggerStateConfiguration : IEntityTypeConfiguration<ProjectTriggerState>
{
    public void Configure(EntityTypeBuilder<ProjectTriggerState> builder)
    {
        builder.ToTable("project_trigger_state");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.SourceKey).HasMaxLength(512).IsRequired();
        builder.HasOne<ProjectTrigger>().WithMany().HasForeignKey(s => s.TriggerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(s => new { s.TriggerId, s.SourceKey }).IsUnique().HasDatabaseName("ux_project_trigger_state_source");
        builder.HasIndex(s => s.NextDueAt).HasFilter("next_due_at IS NOT NULL").HasDatabaseName("ix_project_trigger_state_due");
    }
}
