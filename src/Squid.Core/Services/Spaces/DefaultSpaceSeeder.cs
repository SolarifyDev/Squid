using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Spaces;

public class DefaultSpaceSeeder : IStartable
{
    private readonly ILifetimeScope _scope;

    public DefaultSpaceSeeder(ILifetimeScope scope)
    {
        _scope = scope;
    }

    public void Start()
    {
        try
        {
            SeedAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Default space seeding failed — will retry on next startup");
        }
    }

    private async Task SeedAsync()
    {
        await using var scope = _scope.BeginLifetimeScope();

        var repository = scope.Resolve<IRepository>();
        var unitOfWork = scope.Resolve<IUnitOfWork>();

        var existing = await repository.FirstOrDefaultAsync<Space>(s => s.IsDefault).ConfigureAwait(false);

        if (existing != null) return;

        try
        {
            var space = new Space
            {
                Name = "Default",
                Slug = "default",
                Description = "",
                IsDefault = true,
                Json = "{}",
                TaskQueueStopped = false,
                IsPrivate = false
            };

            await repository.InsertAsync(space).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            Log.Information("Seeded default space {SpaceName}", space.Name);
        }
        catch (Exception ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            Log.Debug("Default space was already created by another instance");
        }
    }
}
