using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class VariableSetConfiguration : IEntityTypeConfiguration<VariableSet>
{
    public void Configure(EntityTypeBuilder<VariableSet> builder)
    {
        builder.ToTable("variable_set");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();

        // The migration enforces Runbook-owned variable sets as one-to-one.
        // The filtered definition lives in SQL because EF's HasFilter is not
        // part of the runtime persistence contract for this table.
    }
}
