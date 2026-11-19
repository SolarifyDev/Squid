using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums;

namespace Squid.Core.Persistence.EntityConfigurations;

public class DeploymentProcessConfiguration: IEntityTypeConfiguration<DeploymentProcess>
{
    public void Configure(EntityTypeBuilder<DeploymentProcess> builder)
    {
        builder.ToTable("deployment_process");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();
        builder.Property(p => p.ProjectId).HasColumnName("project_id").IsRequired();
        builder.Property(p => p.ProcessKind)
            .HasConversion<int>()
            .HasDefaultValue(DeploymentProcessKind.ProjectDeployment)
            .IsRequired();
        builder.Property(p => p.OwnerId);

        builder.ToTable(t => t.HasCheckConstraint(
            "CheckRunbookOwnership",
            "(process_kind = 0 AND owner_id IS NULL) OR (process_kind = 1 AND owner_id IS NOT NULL AND owner_id > 0)"));

        builder.HasIndex(p => p.OwnerId)
            .IsUnique()
            .HasFilter("process_kind = 1");
        builder.HasIndex(p => p.ProjectId);
        builder.HasIndex(p => new { p.SpaceId, p.ProjectId });
    }
}
