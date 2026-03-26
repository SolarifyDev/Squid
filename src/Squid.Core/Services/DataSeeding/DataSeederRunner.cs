namespace Squid.Core.Services.DataSeeding;

public class DataSeederRunner(ILifetimeScope scope) : IStartable
{
    public void Start() => RunAsync().GetAwaiter().GetResult();

    private async Task RunAsync()
    {
        await using var seederScope = scope.BeginLifetimeScope();

        var seeders = seederScope.Resolve<IEnumerable<IDataSeeder>>().OrderBy(s => s.Order).ToList();

        foreach (var seeder in seeders)
        {
            try
            {
                await seeder.SeedAsync(seederScope).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Seeder {SeederType} failed — will retry on next startup", seeder.GetType().Name);
            }
        }

        Log.Information("Data seeding complete — ran {Count} seeders", seeders.Count);
    }
}
