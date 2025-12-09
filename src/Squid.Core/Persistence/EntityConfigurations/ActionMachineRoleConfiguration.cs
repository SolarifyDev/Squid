namespace Squid.Core.Persistence.EntityConfigurations;

public class ActionMachineRoleConfiguration : IEntityTypeConfiguration<ActionMachineRole>
{
    public void Configure(EntityTypeBuilder<ActionMachineRole> builder)
    {
        builder.ToTable("action_machine_roles");

        builder.HasKey(amr => new { amr.ActionId, amr.MachineRole });

        builder.Property(amr => amr.ActionId)
            .IsRequired();

        builder.Property(amr => amr.MachineRole)
            .IsRequired()
            .HasMaxLength(100);
    }
}
