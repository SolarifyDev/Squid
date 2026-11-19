using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class RunbookConfiguration : IEntityTypeConfiguration<Runbook>
{
    public void Configure(EntityTypeBuilder<Runbook> builder)
    {
        builder.ToTable("runbook");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedOnAdd();

        builder.Property(r => r.ProjectId).IsRequired();
        builder.Property(r => r.SpaceId).IsRequired();
        builder.Property(r => r.Name).HasMaxLength(200).IsRequired();
        builder.Property(r => r.Description).HasColumnType("text");
        builder.Property(r => r.TagsJson).HasColumnType("jsonb").HasDefaultValue("[]").IsRequired();
        builder.Property(r => r.ProcessId).IsRequired();
        builder.Property(r => r.VariableSetId).IsRequired();

        builder.HasIndex(r => r.ProcessId).IsUnique();
        builder.HasIndex(r => r.VariableSetId).IsUnique();
        builder.HasIndex(r => new { r.ProjectId, r.Name });
        builder.HasIndex(r => new { r.SpaceId, r.ProjectId });

        builder.HasOne<DeploymentProcess>()
            .WithMany()
            .HasForeignKey(r => r.ProcessId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<VariableSet>()
            .WithMany()
            .HasForeignKey(r => r.VariableSetId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
