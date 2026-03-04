using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class ProjectGroupConfiguration : IEntityTypeConfiguration<ProjectGroup>
{
    public void Configure(EntityTypeBuilder<ProjectGroup> builder)
    {
        builder.ToTable("project_group");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();
    }
}
