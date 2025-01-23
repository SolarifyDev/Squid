using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Squid.Infrastructure.Persistence.EntityConfigurations;

public class LibraryVariableSetConfiguration: IEntityTypeConfiguration<LibraryVariableSet>
{
    public void Configure(EntityTypeBuilder<LibraryVariableSet> builder)
    {
        builder.ToTable("library_variable_set");

        builder.HasKey(p => p.Id);
    }
}