using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ServerTaskConfiguration: IEntityTypeConfiguration<ServerTask>
{
    public void Configure(EntityTypeBuilder<ServerTask> builder)
    {
        builder.ToTable("server_task");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        builder.Property(p => p.DataVersion).IsConcurrencyToken();
        builder.Property(p => p.State).HasMaxLength(50).IsRequired();
        builder.Property(p => p.HasPendingInterruptions);

        builder.HasIndex(p => new { p.ConcurrencyTag, p.State })
            .HasFilter("concurrency_tag IS NOT NULL")
            .HasDatabaseName("ix_server_task_concurrency_tag_state");
    }
}
