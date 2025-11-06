namespace Squid.Core.Persistence.EntityConfigurations;

public class ServerTaskConfiguration: IEntityTypeConfiguration<ServerTask>
{
    public void Configure(EntityTypeBuilder<ServerTask> builder)
    {
        builder.ToTable("server_task");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();
    }
}
