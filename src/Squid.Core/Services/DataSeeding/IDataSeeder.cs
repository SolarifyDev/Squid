namespace Squid.Core.Services.DataSeeding;

public interface IDataSeeder
{
    int Order { get; }
    Task SeedAsync(ILifetimeScope scope);
}
