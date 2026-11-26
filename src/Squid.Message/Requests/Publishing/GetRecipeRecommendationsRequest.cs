using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Publishing;
using Squid.Message.Response;

namespace Squid.Message.Requests.Publishing;

[RequiresPermission(Permission.FeedView)]
public class GetRecipeRecommendationsRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public string PackageId { get; set; }

    public string Version { get; set; }
}

public class GetRecipeRecommendationsResponse : SquidResponse<RecipeRecommendationsDto>
{
}
