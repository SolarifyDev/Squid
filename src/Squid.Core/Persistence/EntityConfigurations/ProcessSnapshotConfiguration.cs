using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Squid.Message.Domain.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ProcessSnapshotConfiguration : IEntityTypeConfiguration<ProcessSnapshot>
{
    public void Configure(EntityTypeBuilder<ProcessSnapshot> builder)
    {
        builder.ToTable("process_snapshot");

        builder.HasKey(p => p.Id);
    }
}
