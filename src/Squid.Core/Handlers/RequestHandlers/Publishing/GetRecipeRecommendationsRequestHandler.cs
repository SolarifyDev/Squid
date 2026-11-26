using Squid.Core.Services.Publishing;
using Squid.Message.Requests.Publishing;

namespace Squid.Core.Handlers.RequestHandlers.Publishing;

public class GetRecipeRecommendationsRequestHandler(IRecipeRecommendationService recommendations) : IRequestHandler<GetRecipeRecommendationsRequest, GetRecipeRecommendationsResponse>
{
    public async Task<GetRecipeRecommendationsResponse> Handle(IReceiveContext<GetRecipeRecommendationsRequest> context, CancellationToken cancellationToken)
    {
        var data = await recommendations.GetAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new GetRecipeRecommendationsResponse { Data = data };
    }
}
