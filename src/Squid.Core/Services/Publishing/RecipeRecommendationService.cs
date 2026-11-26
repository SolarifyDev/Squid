using Squid.Core.Services.Packages.BuiltIn;
using Squid.Core.Services.Packages.BuiltIn.Exceptions;
using Squid.Message.Models.Publishing;
using Squid.Message.Requests.Publishing;

namespace Squid.Core.Services.Publishing;

public sealed class RecipeRecommendationService(IBuiltInFeedLocator feeds, IBuiltInPackageDataProvider packages, IDeployRecipeRegistry registry) : IRecipeRecommendationService
{
    public async Task<RecipeRecommendationsDto> GetAsync(GetRecipeRecommendationsRequest request, CancellationToken ct)
    {
        var feed = await feeds.GetAsync(request.SpaceId.GetValueOrDefault(), ct).ConfigureAwait(false);
        var package = await packages.GetAsync(feed.Id, request.PackageId ?? string.Empty, request.Version ?? string.Empty, ct).ConfigureAwait(false);

        if (package == null)
            throw new BuiltInPackageNotFoundException(feed.Id, request.PackageId, request.Version);

        var facts = PackageFactsJson.Deserialize(package.Facts);

        return facts == null ? new RecipeRecommendationsDto() : new RecipeRecommendationsDto { Inspected = true, Facts = facts, Selection = registry.Select(facts) };
    }
}
