using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public sealed class ProjectTriggerRunConfiguration : IEntityTypeConfiguration<ProjectTriggerRun>
{
    public void Configure(EntityTypeBuilder<ProjectTriggerRun> builder)
    {
        builder.ToTable("project_trigger_run");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.TriggerType).HasMaxLength(64).IsRequired();
        builder.Property(r => r.ActionType).HasMaxLength(64).IsRequired();
        builder.Property(r => r.SourceKey).HasMaxLength(512).IsRequired();
        builder.Property(r => r.OccurrenceKey).HasMaxLength(512).IsRequired();
        builder.Property(r => r.Status).HasMaxLength(32).IsRequired();
        builder.Property(r => r.ConfigurationJson).HasColumnType("jsonb").IsRequired();
        builder.Property(r => r.EvidenceJson).HasColumnType("jsonb").IsRequired();
        builder.Property(r => r.ActionPlanJson).HasColumnType("jsonb");
        builder.HasOne<ProjectTrigger>().WithMany().HasForeignKey(r => r.TriggerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(r => new { r.TriggerId, r.OccurrenceKey }).IsUnique().HasDatabaseName("ux_project_trigger_run_occurrence");
        builder.HasIndex(r => new { r.TriggerId, r.ObservedAt, r.Id }).HasDatabaseName("ix_project_trigger_run_history");
        builder.HasIndex(r => new { r.Status, r.NextAttemptAt, r.Id }).HasDatabaseName("ix_project_trigger_run_due");
        builder.HasIndex(r => r.DeploymentId).HasFilter("deployment_id IS NOT NULL").HasDatabaseName("ix_project_trigger_run_deployment");
    }
}
