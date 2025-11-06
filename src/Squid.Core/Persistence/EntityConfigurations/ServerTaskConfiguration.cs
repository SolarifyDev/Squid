using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Squid.Core.Infrastructure.Domain.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations
{
    /// <summary>
    /// ServerTask 的 EF Core 配置。
    /// </summary>
    public class ServerTaskConfiguration : IEntityTypeConfiguration<ServerTask>
    {
        public void Configure(EntityTypeBuilder<ServerTask> builder)
        {
            builder.ToTable("server_task");

            builder.HasKey(x => x.Id);

            builder.Property(x => x.Id)
                .ValueGeneratedOnAdd();

            builder.Property(x => x.DeploymentId)
                .IsRequired();

            builder.Property(x => x.Status)
                .HasMaxLength(32)
                .IsRequired();

            builder.Property(x => x.Log)
                .HasColumnType("text");

            builder.Property(x => x.CreatedAt)
                .IsRequired();

            builder.Property(x => x.StartedAt);

            builder.Property(x => x.FinishedAt);

            // 可扩展：索引、唯一约束等
        }
    }
}
