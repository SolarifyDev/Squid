using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Persistence.EntityConfigurations;

public class CertificateConfiguration : IEntityTypeConfiguration<Certificate>
{
    public void Configure(EntityTypeBuilder<Certificate> builder)
    {
        builder.ToTable("certificate");

        builder.HasKey(p => p.Id);

        builder.Ignore(p => p.PasswordHasValue);
        builder.Ignore(p => p.IsExpired);
    }
}
