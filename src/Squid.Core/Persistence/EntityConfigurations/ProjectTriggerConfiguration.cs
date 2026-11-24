using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public sealed class ProjectTriggerConfiguration : IEntityTypeConfiguration<ProjectTrigger>
{
    public void Configure(EntityTypeBuilder<ProjectTrigger> builder)
    {
        builder.ToTable("project_trigger");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).HasMaxLength(200).IsRequired();
        builder.Property(t => t.TriggerType).HasMaxLength(64).IsRequired();
        builder.Property(t => t.ActionType).HasMaxLength(64).IsRequired();
        builder.Property(t => t.ConfigurationJson).HasColumnType("jsonb").IsRequired();
        builder.Property(t => t.Revision).IsConcurrencyToken();
        builder.HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(t => new { t.ProjectId, t.IsDeleted, t.Id }).HasDatabaseName("ix_project_trigger_project");
    }
}
