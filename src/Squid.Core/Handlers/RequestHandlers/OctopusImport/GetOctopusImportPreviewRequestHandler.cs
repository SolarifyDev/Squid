using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Requests.OctopusImport;

namespace Squid.Core.Handlers.RequestHandlers.OctopusImport;

public class GetOctopusImportPreviewRequestHandler(
    IOctopusImportSessionDataProvider sessionDataProvider,
    IOctopusImportSessionService sessionService,
    ICurrentUser currentUser,
    IOctopusImportPlanningPipeline planningPipeline)
    : IRequestHandler<GetOctopusImportPreviewRequest, GetOctopusImportPreviewResponse>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<GetOctopusImportPreviewResponse> Handle(
        IReceiveContext<GetOctopusImportPreviewRequest> context,
        CancellationToken cancellationToken)
    {
        var request = context.Message;
        var destinationSpaceId = GetSpaceId(request);
        var session = await GetOwnedSessionAsync(request, cancellationToken).ConfigureAwait(false);
        var state = ParseState(session.State);
        EnsurePreviewAllowed(state);

        if (string.IsNullOrWhiteSpace(session.TemporaryUploadPath) || !File.Exists(session.TemporaryUploadPath))
            return BadRequest(request, state, "Octopus import session does not have an available temporary upload.");

        OctopusImportPlanningSnapshot snapshot;
        try
        {
            snapshot = await planningPipeline
                .BuildPreviewAsync(session.TemporaryUploadPath, destinationSpaceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OctopusArchiveExtractionException ex)
        {
            return BadRequest(request, state, ex.Message);
        }

        var responseSession = state == OctopusImportSessionState.Extracted
            ? await sessionService
                .UpdatePayloadAndTransitionAsync(
                    request.SessionId,
                    destinationSpaceId,
                    OctopusImportSessionState.Extracted,
                    OctopusImportSessionState.Previewed,
                    redactedNormalizedDataJson: JsonSerializer.Serialize(snapshot.PreviewPlan, JsonOptions),
                    ct: cancellationToken)
                .ConfigureAwait(false)
            : await sessionService.GetSessionAsync(request.SessionId, destinationSpaceId, cancellationToken).ConfigureAwait(false);

        return new GetOctopusImportPreviewResponse
        {
            Code = HttpStatusCode.OK,
            Data = new GetOctopusImportPreviewResponseData
            {
                Session = responseSession,
                PreviewPlan = OctopusImportRedaction.RedactDto(snapshot.PreviewPlan)
            }
        };
    }

    private async Task<OctopusImportSession> GetOwnedSessionAsync(GetOctopusImportPreviewRequest request, CancellationToken ct)
    {
        var destinationSpaceId = GetSpaceId(request);
        if (currentUser.Id == null)
            throw new UnauthorizedAccessException("Octopus import preview requires an authenticated user.");

        var session = await sessionDataProvider
            .GetSessionAsync(request.SessionId, currentUser.Id.Value, destinationSpaceId, ct)
            .ConfigureAwait(false);

        if (session == null)
            throw new OctopusImportSessionNotFoundException(request.SessionId);

        return session;
    }

    private static void EnsurePreviewAllowed(OctopusImportSessionState state)
    {
        if (state is OctopusImportSessionState.Extracted or OctopusImportSessionState.Previewed or OctopusImportSessionState.Validated)
            return;

        throw new OctopusImportSessionStateTransitionException(state, OctopusImportSessionState.Previewed);
    }

    private static GetOctopusImportPreviewResponse BadRequest(
        GetOctopusImportPreviewRequest request,
        OctopusImportSessionState state,
        string message)
        => new()
        {
            Code = HttpStatusCode.BadRequest,
            Msg = message,
            Data = new GetOctopusImportPreviewResponseData
            {
                Session = new OctopusImportSessionDto
                {
                    SessionId = request.SessionId,
                    DestinationSpaceId = request.SpaceId ?? 0,
                    State = state
                },
                PreviewPlan = new OctopusImportPreviewPlanDto
                {
                    GeneratedAt = DateTimeOffset.UtcNow,
                    Diagnostics =
                    [
                        OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
                        {
                            Severity = OctopusImportCompatibilitySeverity.Blocker,
                            Code = "OctopusImport.Preview.Unavailable",
                            Message = message
                        })
                    ]
                }
            }
        };

    private static int GetSpaceId(GetOctopusImportPreviewRequest request)
        => request.SpaceId
           ?? throw new UnauthorizedAccessException("Octopus import preview requires destination space context.");

    private static OctopusImportSessionState ParseState(string state)
        => Enum.TryParse<OctopusImportSessionState>(state, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Octopus import session state.");
}
