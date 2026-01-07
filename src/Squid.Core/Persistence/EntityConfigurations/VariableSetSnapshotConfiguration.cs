using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class VariableSetSnapshotConfiguration : IEntityTypeConfiguration<VariableSetSnapshot>
{
    public void Configure(EntityTypeBuilder<VariableSetSnapshot> builder)
    {
        builder.ToTable("variable_set_snapshot");

        builder.HasKey(p => p.Id);
    }
}
