using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class DeploymentExecutionCheckpointConfiguration : IEntityTypeConfiguration<DeploymentExecutionCheckpoint>
{
    public void Configure(EntityTypeBuilder<DeploymentExecutionCheckpoint> builder)
    {
        builder.ToTable("deployment_execution_checkpoint");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        builder.HasIndex(p => p.ServerTaskId).IsUnique();

        builder.Property(p => p.BatchStatesJson).HasColumnType("jsonb").HasDefaultValue("{}");
        // InFlightScriptsJson is a JSON ARRAY (InFlightScriptMap serializes List<Entry>); empty = "[]".
        builder.Property(p => p.InFlightScriptsJson).HasColumnType("jsonb").HasDefaultValue("[]");
    }
}
