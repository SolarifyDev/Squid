namespace Squid.Core.Persistence.EntityConfigurations;

public class PhaseConfiguration : IEntityTypeConfiguration<Phase>
{
    public void Configure(EntityTypeBuilder<Phase> builder)
    {
        builder.ToTable("phase");

        builder.HasKey(p => p.Id);
    }
}