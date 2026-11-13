using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Handlers.CommandHandlers.OctopusImport;

public class ExtractOctopusImportCommandHandler(
    IOctopusImportSessionDataProvider sessionDataProvider,
    IOctopusImportSessionService sessionService,
    ICurrentUser currentUser,
    IOctopusImportSourceReader sourceReader,
    IOctopusManifestInventoryBuilder inventoryBuilder,
    IOctopusResourceGraphBuilder graphBuilder)
    : ICommandHandler<ExtractOctopusImportCommand, ExtractOctopusImportResponse>
{
    public const string HistoryFetchIncompleteCode = "OctopusImport.Extraction.HistoryFetchIncomplete";
    public const string HistoryFetchIncompleteMessage = "The Octopus server history fetch for this session has not finished. Finish the fetch before extracting.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ExtractOctopusImportResponse> Handle(
        IReceiveContext<ExtractOctopusImportCommand> context,
        CancellationToken cancellationToken)
    {
        var command = context.Message;
        var destinationSpaceId = GetSpaceId(command);
        var session = await GetOwnedSessionAsync(command, cancellationToken).ConfigureAwait(false);
        var state = ParseState(session.State);
        if (state != OctopusImportSessionState.Uploaded)
            throw new OctopusImportSessionStateTransitionException(state, OctopusImportSessionState.Extracted);

        // A server session has no file until its fetch finishes, so an unfinished fetch is the reason to report first.
        if (IsHistoryFetchIncomplete(session))
            return BadRequest(command, HistoryFetchIncompleteMessage, [Diagnostic(OctopusImportCompatibilitySeverity.Blocker, HistoryFetchIncompleteCode, HistoryFetchIncompleteMessage)]);

        if (string.IsNullOrWhiteSpace(session.TemporaryUploadPath) || !File.Exists(session.TemporaryUploadPath))
            return BadRequest(command, "Octopus import session does not have an available temporary upload.", [
                Diagnostic(
                    OctopusImportCompatibilitySeverity.Blocker,
                    "OctopusImport.Extraction.TemporaryUploadMissing",
                    "Octopus import session does not have an available temporary upload.")
            ]);

        OctopusInputExtractionResult extraction;

        try
        {
            extraction = await sourceReader.ReadAsync(session.TemporaryUploadPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OctopusArchiveExtractionException ex)
        {
            return BadRequest(command, ex.Message, [
                Diagnostic(
                    OctopusImportCompatibilitySeverity.Blocker,
                    ex.Code,
                    ex.Message,
                    ex.SourcePath)
            ]);
        }

        var inventory = inventoryBuilder.Build(extraction);
        var graph = graphBuilder.Build(inventory);
        var result = BuildResult(extraction, inventory, graph);

        if (result.HasBlockers)
            return BadRequest(command, "Octopus import extraction produced blocking diagnostics.", result.Diagnostics, result);

        var payloadJson = JsonSerializer.Serialize(result, JsonOptions);
        var updatedSession = await sessionService
            .UpdatePayloadAndTransitionAsync(
                command.SessionId,
                destinationSpaceId,
                OctopusImportSessionState.Uploaded,
                OctopusImportSessionState.Extracted,
                redactedNormalizedDataJson: payloadJson,
                ct: cancellationToken)
            .ConfigureAwait(false);

        return new ExtractOctopusImportResponse
        {
            Code = HttpStatusCode.OK,
            Data = new ExtractOctopusImportResponseData
            {
                Session = updatedSession,
                Extraction = result,
                BlockerSummary = OctopusImportBlockerSummaryBuilder.Build(result)
            }
        };
    }

    private async Task<OctopusImportSession> GetOwnedSessionAsync(ExtractOctopusImportCommand command, CancellationToken ct)
    {
        var destinationSpaceId = GetSpaceId(command);
        if (currentUser.Id == null)
            throw new UnauthorizedAccessException("Octopus import extraction requires an authenticated user.");

        var session = await sessionDataProvider
            .GetSessionAsync(command.SessionId, currentUser.Id.Value, destinationSpaceId, ct)
            .ConfigureAwait(false);

        if (session == null)
            throw new OctopusImportSessionNotFoundException(command.SessionId);

        return session;
    }

    // Extracting while a REST history fetch is still running would plan from a bundle that does not exist yet.
    private static bool IsHistoryFetchIncomplete(OctopusImportSession session)
        => OctopusImportSourceSummaries.Parse(session.SourceSummaryJson)?.HistoryFetch is { Done: false };

    private static OctopusImportExtractionResultDto BuildResult(
        OctopusInputExtractionResult extraction,
        OctopusManifestInventoryResult inventory,
        OctopusResourceGraph graph)
    {
        var diagnostics = extraction.Diagnostics
            .Concat(inventory.Diagnostics)
            .Concat(graph.Diagnostics)
            .Select(MapDiagnostic)
            .GroupBy(d => new { d.Severity, d.Code, d.Message, d.SourceId, d.ResourceType, d.ResourceName })
            .Select(g => g.First())
            .ToList();

        return OctopusImportRedaction.RedactDto(new OctopusImportExtractionResultDto
        {
            ExtractedAt = DateTimeOffset.UtcNow,
            DocumentCount = extraction.Documents.Count,
            ResourceCount = graph.Resources.Count,
            Counts = inventory.Counts
                .Select(c => new OctopusImportDocumentCountDto
                {
                    DocumentType = c.Kind.ToString(),
                    Count = c.Count
                })
                .ToList(),
            Files = extraction.Documents
                .OrderBy(d => d.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(d => new OctopusImportSourceFileSummaryDto
                {
                    Path = d.SourcePath,
                    DocumentType = d.Classification.Kind.ToString(),
                    SizeBytes = d.SizeBytes,
                    Sha256 = null
                })
                .ToList(),
            Diagnostics = diagnostics
        });
    }

    private static OctopusImportDiagnosticDto MapDiagnostic(OctopusInputExtractionDiagnostic diagnostic)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = diagnostic.Severity,
            Code = diagnostic.Code,
            Message = diagnostic.Message,
            SourceId = diagnostic.SourceId,
            ResourceType = diagnostic.DocumentKind?.ToString(),
            ResourceName = diagnostic.SourcePath
        });

    private static ExtractOctopusImportResponse BadRequest(
        ExtractOctopusImportCommand command,
        string message,
        IReadOnlyList<OctopusImportDiagnosticDto> diagnostics,
        OctopusImportExtractionResultDto extraction = null)
    {
        extraction ??= new OctopusImportExtractionResultDto
        {
            ExtractedAt = DateTimeOffset.UtcNow,
            Diagnostics = diagnostics.Select(OctopusImportRedaction.RedactDiagnostic).ToList()
        };

        return new ExtractOctopusImportResponse
        {
            Code = HttpStatusCode.BadRequest,
            Msg = message,
            Data = new ExtractOctopusImportResponseData
            {
                Session = new OctopusImportSessionDto
                {
                    SessionId = command.SessionId,
                    DestinationSpaceId = command.SpaceId ?? 0,
                    State = OctopusImportSessionState.Uploaded
                },
                Extraction = extraction,
                BlockerSummary = OctopusImportBlockerSummaryBuilder.Build(extraction)
            }
        };
    }

    private static OctopusImportDiagnosticDto Diagnostic(
        OctopusImportCompatibilitySeverity severity,
        string code,
        string message,
        string sourcePath = null)
        => OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
        {
            Severity = severity,
            Code = code,
            Message = message,
            ResourceName = sourcePath
        });

    private static int GetSpaceId(ExtractOctopusImportCommand command)
        => command.SpaceId
           ?? throw new UnauthorizedAccessException("Octopus import extraction requires destination space context.");

    private static OctopusImportSessionState ParseState(string state)
        => Enum.TryParse<OctopusImportSessionState>(state, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Octopus import session state.");
}
