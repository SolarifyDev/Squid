using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ServerTaskLogConfiguration : IEntityTypeConfiguration<ServerTaskLog>
{
    public void Configure(EntityTypeBuilder<ServerTaskLog> builder)
    {
        builder.ToTable("server_task_log");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        builder.Property(p => p.ServerTaskId).IsRequired();
        builder.Property(p => p.Category).HasMaxLength(50);
        builder.Property(p => p.Source).HasMaxLength(500);
        builder.Property(p => p.SequenceNumber).IsRequired();

        builder.HasIndex(p => p.ServerTaskId);
        builder.HasIndex(p => new { p.ServerTaskId, p.SequenceNumber });
    }
}
