using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ProjectConfiguration: IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("project");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        // Stored as jsonb (see 20260530_add_project_deployment_settings.sql); map the
        // string property accordingly so Npgsql writes jsonb rather than text.
        builder.Property(p => p.DeploymentSettingsJson).HasColumnType("jsonb");
    }
}
