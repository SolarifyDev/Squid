using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class MachineConfiguration: IEntityTypeConfiguration<Machine>
{
    public void Configure(EntityTypeBuilder<Machine> builder)
    {
        builder.ToTable("machine");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();
        builder.HasIndex(p => new { p.Name, p.SpaceId }).IsUnique();
        builder.Property(p => p.Endpoint).HasColumnType("jsonb");
        builder.Property(p => p.HealthStatus)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();
        builder.Property(p => p.HealthLastChecked);
    }
}
