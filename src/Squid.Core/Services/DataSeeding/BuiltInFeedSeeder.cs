using Squid.Core.Services.Packages.BuiltIn;

namespace Squid.Core.Services.DataSeeding;

/// <summary>Gives every space that has none its built-in package feed: the spaces that existed before the feed did, and the default space created just before this runs. A space created later gets its feed from <c>SpaceService</c>.</summary>
public class BuiltInFeedSeeder : IDataSeeder
{
    public int Order => 450;

    public async Task SeedAsync(ILifetimeScope scope)
    {
        var created = await scope.Resolve<IBuiltInFeedProvisioner>().EnsureForAllSpacesAsync(CancellationToken.None).ConfigureAwait(false);

        Log.Information("Built-in feed seeding complete: {Created} feed(s) created", created);
    }
}
