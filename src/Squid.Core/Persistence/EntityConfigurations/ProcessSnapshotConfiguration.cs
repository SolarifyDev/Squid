using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ProcessSnapshotConfiguration : IEntityTypeConfiguration<ProcessSnapshot>
{
    public void Configure(EntityTypeBuilder<ProcessSnapshot> builder)
    {
        builder.ToTable("process_snapshot");

        builder.HasKey(p => p.Id);
    }
}
