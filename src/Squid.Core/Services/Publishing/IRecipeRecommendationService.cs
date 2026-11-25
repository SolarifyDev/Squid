using Squid.Message.Models.Publishing;
using Squid.Message.Requests.Publishing;

namespace Squid.Core.Services.Publishing;

public interface IRecipeRecommendationService : IScopedDependency
{
    /// <summary>The recipes that fit one stored version, from the facts recorded when it was pushed. A version pushed before inspection existed has none, and says so.</summary>
    Task<RecipeRecommendationsDto> GetAsync(GetRecipeRecommendationsRequest request, CancellationToken ct);
}
