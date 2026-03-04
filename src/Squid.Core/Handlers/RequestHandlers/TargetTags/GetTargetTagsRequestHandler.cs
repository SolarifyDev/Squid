using Squid.Core.Services.TargetTags;
using Squid.Message.Models.Deployments.TargetTag;
using Squid.Message.Requests.TargetTag;

namespace Squid.Core.Handlers.RequestHandlers.TargetTags;

public class GetTargetTagsRequestHandler : IRequestHandler<GetTargetTagsRequest, GetTargetTagsResponse>
{
    private readonly IMapper _mapper;
    private readonly ITargetTagDataProvider _dataProvider;

    public GetTargetTagsRequestHandler(IMapper mapper, ITargetTagDataProvider dataProvider)
    {
        _mapper = mapper;
        _dataProvider = dataProvider;
    }

    public async Task<GetTargetTagsResponse> Handle(IReceiveContext<GetTargetTagsRequest> context, CancellationToken cancellationToken)
    {
        var tags = await _dataProvider.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return new GetTargetTagsResponse
        {
            Data = new GetTargetTagsResponseData
            {
                TargetTags = _mapper.Map<List<TargetTagDto>>(tags)
            }
        };
    }
}
